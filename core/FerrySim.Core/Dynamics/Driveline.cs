using System;
using FerrySim.Core.Contracts;

namespace FerrySim.Core.Dynamics
{
	/// <summary>
	/// One engine, its reverse-reduction gear and its propeller shaft. The engine and the shaft are
	/// separate rotating bodies joined by a hydraulic clutch that is open, slipping at its torque
	/// capacity, or locked (shaft speed = ±engine speed / gear ratio). Shaft speed is positive in the
	/// sense that drives the boat ahead and negative going astern.
	/// </summary>
	public sealed class Driveline
	{
		// Shaft friction is smoothed through zero over this speed so a resting shaft stays at rest.
		private const double FrictionSmoothingSpeed = 1.0;

		private readonly EngineModel _engine;
		private readonly PropellerModel _propeller;
		private readonly double _gearRatio;
		private readonly double _efficiency;
		private readonly double _capacity;
		private double _governorIntegral;

		public Driveline(EngineModel engine, PropellerModel propeller, double gearRatio, double gearEfficiency, double clutchCapacity)
		{
			_engine = engine ?? throw new ArgumentNullException(nameof(engine));
			_propeller = propeller ?? throw new ArgumentNullException(nameof(propeller));
			_gearRatio = gearRatio;
			_efficiency = gearEfficiency;
			_capacity = clutchCapacity;
			Reset();
		}

		/// <summary>Crankshaft speed, rad/s.</summary>
		public double EngineSpeed { get; private set; }

		/// <summary>Propeller shaft speed, rad/s, negative astern.</summary>
		public double ShaftSpeed { get; private set; }

		/// <summary>Turbocharger boost as a fraction of full boost.</summary>
		public double Boost { get; private set; }

		/// <summary>True while the clutch is locked (no slip).</summary>
		public bool ClutchLocked { get; private set; }

		/// <summary>Clutch torque at the propeller shaft in the last substep, N·m.</summary>
		public double ClutchTorque { get; private set; }

		/// <summary>Propeller torque load in the last substep, N·m.</summary>
		public double PropellerTorque { get; private set; }

		/// <summary>Engine idling in neutral, shaft at rest.</summary>
		public void Reset()
		{
			EngineSpeed = _engine.IdleSpeed;
			ShaftSpeed = 0.0;
			ClutchLocked = false;
			ClutchTorque = 0.0;
			PropellerTorque = 0.0;
			var friction = _engine.FrictionTorque(EngineSpeed);
			_governorIntegral = friction;
			Boost = friction / (_engine.MaximumBrakeTorque(EngineSpeed) + friction);
		}

		/// <summary>
		/// Advances <paramref name="dt"/> in <paramref name="substeps"/> equal parts with the propeller
		/// advance speed held. <paramref name="direction"/> is the pressurised clutch (+1 ahead, −1 astern,
		/// 0 none) and <paramref name="capacityFraction"/> its pressure as a fraction of full.
		/// </summary>
		public void Step(double dt, int substeps, double throttle, int direction, double capacityFraction, double advanceSpeed)
		{
			if (direction == 0 || capacityFraction <= 0.0)
			{
				ClutchLocked = false;
			}

			var h = dt / substeps;
			var setPoint = _engine.SpeedSetPoint(throttle);
			var g = _gearRatio;
			var engineInertia = _engine.Inertia;
			var shaftInertia = _propeller.ShaftInertia;
			for (var k = 0; k < substeps; k++)
			{
				var engineSpeed = EngineSpeed;
				var shaftSpeed = ShaftSpeed;

				// PI governor, limited by the full-load curve and by boost, with conditional integration.
				var friction = _engine.FrictionTorque(engineSpeed);
				var fullLoad = _engine.MaximumBrakeTorque(engineSpeed) + friction;
				var available = fullLoad * (_engine.UnboostedTorqueFraction + (1.0 - _engine.UnboostedTorqueFraction) * Boost);
				var error = setPoint - engineSpeed;
				var command = _engine.ProportionalGain * error + _governorIntegral;
				var combustion = Math.Clamp(command, 0.0, available);
				var saturated = (command >= available && error > 0.0) || (command <= 0.0 && error < 0.0);
				if (!saturated)
				{
					_governorIntegral = Math.Clamp(_governorIntegral + _engine.IntegralGain * error * h, 0.0, fullLoad);
				}

				Boost += (Math.Clamp(command / fullLoad, 0.0, 1.0) - Boost) * h / _engine.TurbochargerTimeConstant;

				_propeller.Evaluate(shaftSpeed, advanceSpeed, out _, out var load);
				PropellerTorque = load;
				var shaftDrag = _propeller.ShaftFrictionTorque * Math.Tanh(shaftSpeed / FrictionSmoothingSpeed);
				var engineNet = combustion - friction;

				if (direction == 0 || capacityFraction <= 0.0)
				{
					ClutchTorque = 0.0;
					engineSpeed += engineNet / engineInertia * h;
					shaftSpeed += (-load - shaftDrag) / shaftInertia * h;
				}
				else
				{
					// Clutch pressure is modulated down as the engine is pulled below idle, so it slips instead of stalling.
					var idle = _engine.IdleSpeed;
					var antiStall = Math.Clamp((engineSpeed - 0.5 * idle) / (0.35 * idle), 0.0, 1.0);
					var capacity = _capacity * Math.Min(1.0, capacityFraction) * antiStall;
					var slipping = !ClutchLocked;
					if (ClutchLocked)
					{
						var drive = direction * g * (engineNet >= 0.0 ? _efficiency * engineNet : engineNet / _efficiency);
						var acceleration = (drive - load - shaftDrag) / (shaftInertia + engineInertia * g * g);
						var required = shaftInertia * acceleration + load + shaftDrag;
						var locked = shaftSpeed + acceleration * h;
						if (Math.Abs(required) <= capacity && direction * locked >= 0.0)
						{
							ClutchTorque = required;
							shaftSpeed = locked;
							engineSpeed = direction * g * shaftSpeed;
						}
						else
						{
							ClutchLocked = false;
							slipping = true;
						}
					}

					if (slipping)
					{
						var slip = direction * engineSpeed / g - shaftSpeed;
						var torque = capacity * Math.Sign(slip);
						ClutchTorque = torque;
						var nextEngine = engineSpeed + (engineNet - direction * torque / g) / engineInertia * h;
						var nextShaft = shaftSpeed + (torque - load - shaftDrag) / shaftInertia * h;
						var nextSlip = direction * nextEngine / g - nextShaft;
						if (capacity > 0.0 && slip * nextSlip <= 0.0)
						{
							// The speeds met within the substep: lock, conserving angular momentum about the shaft.
							var common = (shaftInertia * nextShaft + engineInertia * g * direction * nextEngine) / (shaftInertia + engineInertia * g * g);
							if (direction * common >= 0.0)
							{
								nextShaft = common;
								nextEngine = direction * g * common;
								ClutchLocked = true;
							}
						}

						engineSpeed = nextEngine;
						shaftSpeed = nextShaft;
					}
				}

				EngineSpeed = engineSpeed > 0.0 ? engineSpeed : 0.0;
				ShaftSpeed = shaftSpeed;
			}
		}
	}
}

"""SI dimensions mirrored from GreyboxDimensions and VesselLightLayout.ForGreybox.

The vessel JSON supplies particulars, hull spacing and windage. Cabin proportions,
wheelhouse height and lantern positions remain the simulator's documented estimates.
All public vectors use Unity x starboard, y up, z forward from design waterline.
"""
import csv


def dimensions(v):
	p, h, w = v['particulars'], v['hull'], v['windage']
	length, beam, depth, draft = (p[k]['value'] for k in ('lengthOverall', 'beam', 'depth', 'draft'))
	freeboard = depth - draft
	cabin_height = min(5., max(2., w['frontalArea']['value'] / beam - max(.5, freeboard)))
	cabin_length = min(.85 * length, max(.3 * length, (w['lateralArea']['value'] - length * max(.5, freeboard)) / cabin_height))
	return dict(length=length, beam=beam, depth=depth, draft=draft,
		demihull_beam=h['demihullBeam']['value'], separation=h['demihullSeparation']['value'],
		freeboard=freeboard, cabin_height=cabin_height, cabin_length=cabin_length,
		eye_height=freeboard + cabin_height + 1.7, eye_forward=.5 * cabin_length - 2., wheelhouse_height=2.2)


def mounts(d):
	top = d['freeboard'] + d['cabin_height']
	mast = [0, top + 3.7, d['eye_forward'] - 1.]
	side = [.5 * (d['beam'] - .4), top + .5, d['eye_forward'] - 1.5]
	anchor = [0, mast[1] + .6, mast[2]]
	return dict(Eye=[0, d['eye_height'], d['eye_forward']],
		Bow=[0, d['freeboard'], .5 * d['length']], Masthead=mast,
		PortSidelight=[-side[0], side[1], side[2]], StarboardSidelight=side,
		Sternlight=[0, d['freeboard'] + 1., -.49 * d['length'] + .1],
		ForwardAnchor=anchor, AfterAnchor=anchor,
		AfterMasthead=[0, mast[1] + 4.5, mast[2] - .5 * d['length']])


def audited_classes(root):
	with (root / 'data-pipeline/calibration/identity-audit.csv').open() as file:
		return {int(r['mmsi']): 't-class' if r['class'] == 'T' else 'k-class'
			for r in csv.DictReader(file) if r['class'] == 'T' or r['class'].startswith('K-')}

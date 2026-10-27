"""Small synthetic runtime pack and vectors from the production Python scalar sampler.
Run: PYTHONPATH=data-pipeline uv run --project data-pipeline python core/dotnet/FerrySim.Core.Tests/World/make_fixture.py
"""
import json
import random
import struct
from pathlib import Path
from ferrydata.harbor_runtime.pack import FILES, digest, sha, write_json
from ferrydata.harbor_runtime.geometry import sample_geoid

out = Path(__file__).with_name('Fixtures')
out.mkdir(exist_ok=True)
ring = [[0.,0.],[20.,0.],[20.,10.],[0.,10.],[0.,0.]]
polygon = dict(type='Polygon', coordinates=[ring])
span = dict(id='enc:span', source_id='span', geometry_ne_m=polygon, vertical_bounds_navd88_m=[None,None],
    pier_ids=['enc:pier'], pier_inventory_complete=False, clearances=[dict(clearance_datum='MHW', vertical_clearance_m=12., underside_navd88_m=None)])
pier = dict(id='enc:pier', source_id='pier', geometry_ne_m=dict(type='LineString',coordinates=[[0.,0.],[20.,0.]]),
    vertical_bounds_navd88_m=[None,None], membership_evidence=[dict(span_id='enc:span', confidence='geometry-derived')])
write_json(out/'aids.json', dict(format='ferrysim-harbor-aids/1',aids=[dict(id='enc:aid',source_id='aid',position_ne_m=[12.5,25.25],position_lonlat=[-74.,40.7],properties=dict(name='Fixture buoy',lights=[dict(period_s=4.,characteristic='Fl')]))]))
write_json(out/'bridges.json',dict(format='ferrysim-harbor-bridges/1',bridges=[dict(id='bridge:span',names=['Fixture'],source_ids=['span'],spans=[span],piers=[pier],audit_evidence={},grouping='fixture',tower_extent_m=None)]))
write_json(out/'contacts.json',[])
write_json(out/'landings.json',dict(format='ferrysim-harbor-landings/1',stops=[dict(stop_id='1',stop_name='Fixture Landing',north_m=10.,east_m=5.,lat=40.7,lon=-74.,tile='tile_0_0',berths=[dict(id='berth:1',structure_id='barge:1',berth_name='Main berth',primary_face=True,berth_order=0,berth_face_segment_ids=[1])])],barges=[dict(id='barge:1',floating=True,geometry_ne_m=polygon,stop_ids=['1'],vertical_bounds_navd88_m=[None,None],provenance=[dict(source='synthetic',confidence=['estimated'])])]))
write_json(out/'geoid.json',dict(format='ferrysim-harbor-geoid/1',cell_m=250.,rows=3,cols=3,origin_north_m=0.,origin_east_m=0.,values_type='little-endian float64',layout='row-major south-to-north; columns west-to-east',values_file='geoid.f64',mask_file='geoid_cells.u8',mask_shape=[2,2]))
(out/'geoid.f64').write_bytes(struct.pack('<9d',-32.,-31.,-30.,-29.,-27.,-25.,-26.,-23.,-20.))
(out/'geoid_cells.u8').write_bytes(bytes([1,0,1,1]))
write_json(out/'footprints.json',dict(format='ferrysim-harbor-queries/1',coverage_ne_m=dict(type='Polygon',coordinates=[[[0,0],[500,0],[500,500],[0,500],[0,0]]]),footprints=[dict(id='barge:barge:1',semantic='barge',source_ids=['barge:1'],geometry_ne_m=polygon,collision_authority=False,vertical_bounds_navd88_m=[None,None],properties=dict(floating=True))]))
write_json(out/'manifest.json',dict(format='ferrysim-harbor-runtime/1',region='harbor',origin_latlon=[40.7,-74.015],environment_depth_digest='0'*64,files={n:sha(out/n) for n in FILES},counts=dict(stops=1,berths=1,barges=1,aids=1,bridges=1,spans=1,footprints=1)))
(out/'DIGEST').write_text(digest(out)+'\n')
# Keep vectors outside the verified directory's exact inventory.
rng=random.Random(20261026)
for name,folder in [('fixture',out),('harbor',Path('data/processed/harbor/runtime'))]:
    m=json.loads((folder/'geoid.json').read_text());lines=['north,east,covered,value']
    for i in range(200):
        north=m['origin_north_m']+rng.uniform(-.05,m['rows']-1+.05)*m['cell_m']
        east=m['origin_east_m']+rng.uniform(-.05,m['cols']-1+.05)*m['cell_m']
        try: value=sample_geoid(folder,north,east);covered=1
        except ValueError: value=0.;covered=0
        lines.append(f'{north:.17g},{east:.17g},{covered},{value:.17g}')
    Path(__file__).with_name(name+'-geoid-vectors.csv').write_text('\n'.join(lines)+'\n')

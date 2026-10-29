"""Failure modes: overlap/scale seams, datum drift, missing cache, invented roofs,
frame drift, corrupt inventory/index, and nondeterministic output bytes."""
import json
from pathlib import Path

import pytest
from shapely.geometry import Point, box, mapping

from ferrydata.harbor_chart import build as chart
from ferrydata.harbor_chart.pack import verify


def record(id_, geometry, cell='fine', band=5, **props):
	return dict(source_id=id_, cell=cell, usage_band=band, feature_class='LNDARE', geometry=geometry, properties=props)


def test_scale_ownership_clips_coarse_and_same_band_overlap():
	cells=[dict(name='fine',usage_band=5,scale=12000,coverage=box(0,0,10,10)),
		dict(name='coarse',usage_band=4,scale=40000,coverage=box(0,0,20,10))]
	rows=[record('a',box(0,0,20,10),'coarse',4),record('b',box(0,0,10,10))]
	out=chart.merge_records(rows,chart.owners(cells,box(0,0,20,10)))
	assert len(out)==2
	from shapely.geometry import shape
	a,b=[shape(r['geometry_ne_m']) for r in out]
	assert a.intersection(b).area==0
	assert a.union(b).area==200


def test_same_source_merges_fragments_without_duplicate_ids():
	cells=[dict(name='a',usage_band=5,scale=12000,coverage=box(0,0,10,10)),dict(name='b',usage_band=5,scale=12000,coverage=box(5,0,20,10))]
	rows=[record('same',box(0,0,10,10),'a'),record('same',box(5,0,20,10),'b')]
	out=chart.merge_records(rows,chart.owners(cells,box(0,0,20,10)))
	assert len(out)==1
	assert out[0]['cells']==['a','b']


def test_datum_override_must_match_declared_mllw():
	assert chart.check_datum('x',12,{'VERDAT':None})=='MLLW'
	assert chart.check_datum('x',12,{'VERDAT':12})=='MLLW'
	with pytest.raises(ValueError,match='datum'):
		chart.check_datum('x',12,{'VERDAT':16})
	with pytest.raises(ValueError,match='datum'):
		chart.check_datum('x',16,{})


def test_missing_cell_is_offline_failure(tmp_path,monkeypatch):
	monkeypatch.setattr(chart.paths,'raw_dir',lambda _:tmp_path)
	with pytest.raises(ValueError,match='missing cached'):
		chart.cached_cell('NO_SUCH_CELL',{})


@pytest.mark.parametrize('basis,ground,expected',[('default-estimate','nyc-groundelev-feet',None),('levels-estimate','nyc-groundelev-feet',None),('height','terrain-navd88-estimate',None),('heightroof-feet','nyc-groundelev-feet',14.5)])
def test_unknown_roof_stays_null(basis,ground,expected):
	assert chart.roof_height(dict(height_basis=basis,ground_basis=ground,roof_navd88_m=14.5))==expected


def fixture_inputs():
	layers={name:[] for name in chart.LAYERS}
	layers['land']=[dict(id='enc:one',source_id='one',cells=['x'],feature_class='LNDARE',properties={},geometry_ne_m=mapping(box(-1,-1,501,501)))]
	layers['soundings']=[dict(id='enc:s:0',source_id='s',cells=['x'],feature_class='SOUNDG',properties={'depth_m':3.2},geometry_ne_m=mapping(Point(250,250)))]
	meta=dict(origin_latlon=[40.7,-74.015],coordinate_frame='test NED',runtime_digest='a'*64,sources=[],cells=[],simplification_m={'land':1.,'buildings':.35})
	return layers,meta


def test_pack_determinism_and_integrity(tmp_path):
	layers,meta=fixture_inputs()
	for name in ['a','b']:
		chart.publish(tmp_path/name,layers,meta)
	assert {p.name:p.read_bytes() for p in (tmp_path/'a').iterdir()}=={p.name:p.read_bytes() for p in (tmp_path/'b').iterdir()}
	verify(tmp_path/'a')
	p=tmp_path/'a/land.json';p.write_bytes(p.read_bytes()+b' ')
	with pytest.raises(ValueError,match='digest|hash'):
		verify(tmp_path/'a')


def test_index_closed_edges_and_corners():
	layers,_=fixture_inputs();payload=chart.layer_payload(layers['land'])
	assert payload['grid']['cell_m']==250
	assert payload['grid']['cells']['1,1']==[0]
	assert payload['grid']['cells']['2,2']==[0]
	assert payload['grid']['cells']['-1,-1']==[0]


def test_missing_file_and_frame_are_refused(tmp_path):
	layers,meta=fixture_inputs();chart.publish(tmp_path/'pack',layers,meta)
	with pytest.raises(ValueError,match='frame'):
		verify(tmp_path/'pack',dict(origin_latlon=[0,0],coordinate_frame='test NED'))
	(tmp_path/'pack/land.json').unlink()
	with pytest.raises(ValueError):
		verify(tmp_path/'pack')


def test_quantisation_is_at_most_half_centimetre_per_axis():
	from shapely import get_coordinates
	import numpy as np
	g=box(-.0051,249.9951,500.0049,501.0051)
	q=chart.quantise(g)
	assert np.max(np.abs(get_coordinates(g)-get_coordinates(q)))<=.005+1e-12


def test_attribute_table_preserves_depth_and_deduplicates():
	layers,_=fixture_inputs()
	rows=layers['soundings'];rows.append(dict(rows[0],id='enc:s:1',geometry_ne_m=mapping(Point(300,300))))
	p=chart.layer_payload(rows)
	assert len(p['attributes'])==1
	assert p['attributes'][0]['depth_m']==3.2
	assert [r[p['fields'].index('attribute_index')] for r in p['features']]==[0,0]

"""A candidate destination may not overlap the immutable source tree."""
import pytest
from ferrydata.harbor_landings.candidate import build


@pytest.mark.parametrize('relation', ['same', 'child', 'parent'])
def test_candidate_refuses_overlapping_source_destination(tmp_path, relation):
	source = tmp_path / 'source'
	source.mkdir()
	destination = {'same': source, 'child': source / 'nested', 'parent': tmp_path}[relation]
	with pytest.raises(ValueError, match='separate'):
		build(source, destination)
	assert list(source.iterdir()) == []

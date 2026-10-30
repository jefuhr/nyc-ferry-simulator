"""Offline busiest shipped AIS presence minute; no report interpolation across segment gaps."""
import argparse
import json
import math
from pathlib import Path
import struct


def peak(folder):
	groups={}
	for vessel,segment,t,n,e,sog,cog,heading in struct.iter_unpack('<iidddfff',(folder/'reports.bin').read_bytes()):
		key=(vessel,segment)
		if key in groups:groups[key][1]=max(t,groups[key][1])
		else:groups[key]=[t,t]
	diff=[0]*1441
	for start,end in groups.values():
		a=max(0,math.ceil(start/60));b=min(1439,math.floor(end/60))
		if a<=b:diff[a]+=1;diff[b+1]-=1
	running=0;counts=[]
	for minute in range(1440):running+=diff[minute];counts.append(running)
	value=max(counts)
	return {'date':folder.name,'minute':counts.index(value),'present_vessels':value,'count_at_738':counts[738]}


if __name__=='__main__':
	p=argparse.ArgumentParser();p.add_argument('traffic',type=Path);p.add_argument('output',type=Path);a=p.parse_args()
	results=[peak(f) for f in sorted(a.traffic.iterdir()) if (f/'reports.bin').is_file()]
	result={'method':'Inclusive shipped vessel/segment presence at integer local minutes, matching TrafficReplay presence; all 28 days, no gaps synthesized; own-vessel suppression happens afterward','daily_peaks':results,'global_peak':max(results,key=lambda x:x['present_vessels'])}
	a.output.write_text(json.dumps(result,indent=2)+'\n')
	print(json.dumps(result['global_peak']))

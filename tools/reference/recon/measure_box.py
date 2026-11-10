"""Synthetic-only acceptance: fit the ground/top planes and box footprint.

The reconstruction has arbitrary rotation and scale. Use a known 2 m top edge
as the single scale anchor; check independent 1 m width and 0.5 m height.
This fixture analyzer is not a general boat measurement or segmentation tool.
"""
import json
import math
from pathlib import Path
import random
import statistics
import sys

import bpy
from mathutils import Vector

sys.path.insert(0,str(Path(__file__).resolve().parent))
from render_views import import_geometry

args=sys.argv[sys.argv.index('--')+1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
_,vertices,faces=import_geometry(Path(args[0]))
random.seed(7)
points=[Vector(v) for v in vertices]
sample=random.sample(points,min(8000,len(points)))
span=math.sqrt(sum((max(p[i] for p in points)-min(p[i] for p in points))**2 for i in range(3)))
threshold=span*.004
best=[]
for _ in range(400):
	a,b,c=random.sample(sample,3); normal=(b-a).cross(c-a)
	if normal.length<1e-8: continue
	normal.normalize(); inliers=[p for p in sample if abs((p-a).dot(normal))<threshold]
	if len(inliers)>len(best): best=inliers; ground=a.copy(); ground_normal=normal.copy()
ground_inliers=len(best)
heights=[(p-ground).dot(ground_normal) for p in points]
if statistics.median(h for h in heights if abs(h)>threshold*3)<0:
	ground_normal=-ground_normal; heights=[-h for h in heights]
# Match the spike's 200-bin top-face detector; no dimensional target is used.
above=[h for h in heights if h>threshold*6]
if not above: raise ValueError('No box points above ground')
minimum,maximum=min(above),max(above); step=(maximum-minimum)/200
bins=[0]*200
for h in above: bins[min(199,int((h-minimum)/step))]+=1
mode=max(range(200),key=lambda i:bins[i]); level=minimum+(mode+.5)*step
top=[p for p,h in zip(points,heights) if h>threshold*6 and abs(h-level)<level*.06]
level=statistics.median((p-ground).dot(ground_normal) for p in top)
u=ground_normal.cross(Vector((1,0,0)))
if u.length<.1: u=ground_normal.cross(Vector((0,1,0)))
u.normalize(); w=ground_normal.cross(u)
xy=[((p-ground).dot(u),(p-ground).dot(w)) for p in top]
centre=(statistics.median(p[0] for p in xy),statistics.median(p[1] for p in xy))
xy=[(x-centre[0],y-centre[1]) for x,y in xy]
radius=statistics.median(math.hypot(x,y) for x,y in xy)
xy=[(x,y) for x,y in xy if math.hypot(x,y)<radius*4]
best=None
for i in range(360):
	angle=math.radians(i*.25); c,s=math.cos(angle),math.sin(angle)
	xs=sorted(x*c-y*s for x,y in xy); ys=sorted(x*s+y*c for x,y in xy)
	# Isolated floating triangles are not box edges. Trim only the outer .1%.
	trim=0; ends=-1
	length,width=xs[ends]-xs[trim],ys[ends]-ys[trim]
	if best is None or length*width<best[0]: best=(length*width,length,width,angle,xs[0],xs[-1],ys[0],ys[-1])
raw=sorted(best[1:3],reverse=True)+[level]
angle=best[3]; c,s=math.cos(angle),math.sin(angle)
qa=(best[4],best[6]); qb=(best[5],best[6]) if best[1]>=best[2] else (best[4],best[7])
def world(q):
	x=q[0]*c+q[1]*s+centre[0]; y=-q[0]*s+q[1]*c+centre[1]
	return list(ground+u*x+w*y+ground_normal*level)
known_edge_points=[world(qa),world(qb)]
factor=2/raw[0]; scaled=[v*factor for v in raw]; expected=[2,1,.5]
result=dict(raw_dimensions=raw,scale_factor=factor,scaled_dimensions_m=scaled,expected_m=expected,error_percent=[100*(v-e)/e for v,e in zip(scaled,expected)],known_edge_m=2,top_points=len(top),ground_inliers=ground_inliers,known_edge_points=known_edge_points,edge_method='fitted top-face rectangle corners in reconstruction coordinates')
Path(args[1]).write_text(json.dumps(result,sort_keys=True,indent='\t')+'\n')
# Also export geometry for the actual recon scale test with a picked known edge.
Path(args[2]).write_text(''.join('v '+' '.join(format(v,'.17g') for v in p)+'\n' for p in vertices)+''.join('f '+' '.join(str(i+1) for i in face)+'\n' for face in faces))
print(json.dumps(result,sort_keys=True))

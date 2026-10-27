"""Repeatable matplotlib QA views of generated footprints and actual curved GLB triangles."""
import argparse
from pathlib import Path
import numpy as np
import shapely
from shapely.geometry import shape,Point
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.collections import PolyCollection
from mpl_toolkits.mplot3d.art3d import Poly3DCollection
from ferrydata import geodesy,paths
from ferrydata.buildings import geometry
from ferrydata.buildings.build import default_output,read_attributes,land_and_exclusions
from ferrydata.world3d import glb,frames,heights
from ferrydata.world3d.build import read_json,active_tiles

CREDIT='Building data: NYC Open Data / OTI; © OpenStreetMap contributors (ODbL), Geofabrik. Visual estimates, not for navigation.'


def render(out,report,harbor=None):
	out,report=Path(out),Path(report)
	harbor=Path(harbor or paths.processed_dir('harbor'))
	report.mkdir(parents=True,exist_ok=True)
	index=read_json(out/'index.json')
	active,_=active_tiles(harbor)
	datum=heights.Sources(harbor,active)
	land=shapely.union_all([land_and_exclusions(harbor/'tiles'/frames.tile_id(i,j))[0] for i,j in active])
	n,e=geodesy.to_local(40.72,-74.)
	manhattan=next(p for p in geometry.polygons(land) if p.covers(Point(float(e),float(n))))
	shapely.prepare(manhattan)
	coverage='NJ uniform snapshot complete' if index['coverage']['complete'] else 'NJ coverage incomplete'
	fig,ax=plt.subplots(figsize=(11,11),facecolor='#182936')
	ax.set_facecolor('#243f50')
	land_polys=[np.asarray(p.exterior.coords)/1000 for p in geometry.polygons(land)]
	ax.add_collection(PolyCollection(land_polys,facecolors='#456052',linewidths=0,rasterized=True))
	for tile in index['tiles']:
		if not tile['parts']:
			continue
		attrs=read_attributes(out/'tiles'/tile['id'])
		polys=[np.asarray(a['footprint']['coordinates'][0])/1000 for a in attrs]
		colors=[a['height_m'] for a in attrs]
		collection=PolyCollection(polys,array=np.array(colors),cmap='YlOrRd',clim=(0,300),linewidths=0,rasterized=True)
		ax.add_collection(collection)
		i,j=tile['i'],tile['j']
		ax.plot(np.array([j,j+1,j+1,j,j])*4,np.array([i,i,i+1,i+1,i])*4,color='white',alpha=.17,lw=.5)
	ax.autoscale_view();ax.set_aspect('equal');ax.tick_params(colors='white')
	ax.set_xlabel('World east (km)',color='white');ax.set_ylabel('World north (km)',color='white')
	ax.set_title(f"Harbor LoD1 · policy {index['selection']['id']} · {index['counts']['buildings']:,} buildings\n{coverage}",color='white')
	bar=fig.colorbar(collection,ax=ax,shrink=.65,label='Building height above ground (m)')
	bar.ax.yaxis.label.set_color('white');bar.ax.tick_params(colors='white')
	fig.text(.02,.01,CREDIT,color='white',fontsize=7)
	fig.savefig(report/'topdown.png',dpi=150,bbox_inches='tight');plt.close(fig)
	views=[('manhattan-east-side-water',(-1500,0,6500,9000),16,-35),('jersey-city-water',(-6200,0,-1500,6000),16,-25)]
	for name,(e0,n0,e1,n1),elevation,azimuth in views:
		fig=plt.figure(figsize=(13,6),facecolor='#d8e5ea')
		ax=fig.add_subplot(111,projection='3d');ax.set_facecolor('#d8e5ea')
		triangles=[]
		for tile in index['tiles']:
			if not tile['parts']:
				continue
			folder=out/'tiles'/tile['id']
			attrs=read_attributes(folder)
			chosen=[]
			for a in attrs:
				p=shape(a['footprint']);xy=np.asarray(p.exterior.coords)
				if xy[:,0].max()<e0 or xy[:,0].min()>e1 or xy[:,1].max()<n0 or xy[:,1].min()>n1:
					continue
				if name.startswith('manhattan') and not manhattan.covers(p.representative_point()):
					continue
				if name.startswith('jersey') and a['source']!='osm-nj':
					continue
				chosen.append(a)
			if not chosen:
				continue
			transform=frames.TileFrame(tile['i'],tile['j']).transform()
			for mesh in sorted({a['mesh'] for a in chosen}):
				_,arrays=glb.decode((folder/mesh).read_bytes())
				xyz=frames.ecef_from_content(arrays['POSITION'],transform)
				lat,lon,h=geodesy.from_ecef(xyz[:,0],xyz[:,1],xyz[:,2])
				n,e=geodesy.to_local(lat,lon)
				navd=h-datum.geoid_at(np.round(n,3),np.round(e,3))
				positions=np.stack([e,n,navd],axis=-1)
				for a in chosen:
					if a['mesh']!=mesh:
						continue
					start=a['index_start']
					idx=arrays['indices'][start:start+a['indices']].reshape(-1,3)
					triangles.extend(positions[idx])
		if triangles:
			tri=np.asarray(triangles)
			cross=np.cross(tri[:,1]-tri[:,0],tri[:,2]-tri[:,0])
			unit=cross/np.maximum(np.linalg.norm(cross,axis=1,keepdims=True),1e-12)
			sun=np.array([-.4,-.5,.77]);sun/=np.linalg.norm(sun)
			shade=.45+.5*np.clip(unit@sun,0,1)
			colors=np.stack([shade*.74,shade*.78,shade*.83,np.ones(len(shade))],axis=-1)
			ax.add_collection3d(Poly3DCollection(tri,facecolors=colors,edgecolors='none',zsort='average',rasterized=True))
		ax.set_xlim(e0,e1);ax.set_ylim(n0,n1);ax.set_zlim(-10,650)
		ax.set_box_aspect((e1-e0,n1-n0,660));ax.view_init(elev=elevation,azim=azimuth)
		ax.set_xlabel('East (m)');ax.set_ylabel('North (m)');ax.set_zlabel('NAVD88 (m)')
		ax.set_title(f'Generated GLB · {name.replace("-"," ").title()}\n{coverage}')
		fig.text(.03,.02,CREDIT,fontsize=7)
		fig.savefig(report/f'oblique-{name}.png',dpi=150,bbox_inches='tight');plt.close(fig)


if __name__=='__main__':
	parser=argparse.ArgumentParser(description=__doc__)
	parser.add_argument('--harbor',type=Path,default=paths.processed_dir('harbor'))
	parser.add_argument('--out',type=Path,default=default_output())
	parser.add_argument('--report',type=Path,default=Path('artifacts/skyline-data/final'))
	args=parser.parse_args()
	render(args.out,args.report,args.harbor)

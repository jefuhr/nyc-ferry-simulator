"""Normalized least-squares plane homography without numerical dependencies."""
import math
import reference as ref


def solve(matrix, rhs):
	"""Partial pivot elimination for a small, normalized full-rank system."""
	n = len(rhs); a = [list(row) + [b] for row,b in zip(matrix,rhs)]
	for col in range(n):
		pivot = max(range(col,n), key=lambda i:abs(a[i][col]))
		if abs(a[pivot][col]) < 1e-10:
			raise ValueError('degenerate homography point set')
		a[col],a[pivot] = a[pivot],a[col]
		scale = a[col][col]; a[col] = [v/scale for v in a[col]]
		for i in range(n):
			if i != col:
				scale=a[i][col]; a[i]=[x-scale*y for x,y in zip(a[i],a[col])]
	return [a[i][-1] for i in range(n)]


def normalize(points):
	cx=sum(x for x,y in points)/len(points); cy=sum(y for x,y in points)/len(points)
	rms=math.sqrt(sum((x-cx)**2+(y-cy)**2 for x,y in points)/len(points))
	if rms < 1e-10: raise ValueError('degenerate homography point set')
	scale=math.sqrt(2)/rms
	return [((x-cx)*scale,(y-cy)*scale) for x,y in points], (cx,cy,scale)


def fit(calibration):
	image=list(map(ref.point,calibration['points'])); plane=list(map(ref.point,calibration['plane_m']))
	if len(image)!=len(plane) or len(image)<4:
		raise ValueError('plane-homography requires four or more matching image and plane_m points')
	tolerance=ref.finite(calibration['tolerance_px'],'reprojection tolerance_px',True)
	p, pn=normalize(plane); q, qn=normalize(image)
	rows=[]; rhs=[]
	for (x,y),(u,v) in zip(p,q):
		rows += [[x,y,1,0,0,0,-u*x,-u*y],[0,0,0,x,y,1,-v*x,-v*y]]; rhs += [u,v]
	# Normalization bounds the condition number for ordinary panels. Rank-deficient
	# and nearly singular normal equations fail closed rather than returning a scale.
	normal=[[sum(row[i]*row[j] for row in rows) for j in range(8)] for i in range(8)]
	vector=[sum(row[i]*b for row,b in zip(rows,rhs)) for i in range(8)]
	h=solve(normal,vector)+[1]
	def project(x,y):
		den=h[6]*x+h[7]*y+1
		if abs(den)<1e-10: raise ValueError('degenerate homography at plane horizon')
		return ((h[0]*x+h[1]*y+h[2])/den,(h[3]*x+h[4]*y+h[5])/den)
	errors=[math.dist(project(x,y),(u,v))/qn[2] for (x,y),(u,v) in zip(p,q)]
	maximum=max(errors); rms=math.sqrt(sum(e*e for e in errors)/len(errors))
	if maximum>tolerance:
		raise ValueError(f'reprojection error {maximum:.6f} px exceeds tolerance {tolerance:g} px')
	def convert(raw):
		u,v=ref.point(raw); u=(u-qn[0])*qn[2]; v=(v-qn[1])*qn[2]
		# Solve the two cross-multiplied inverse projection equations at this point.
		a,b=h[0]-u*h[6],h[1]-u*h[7]; c,d=h[3]-v*h[6],h[4]-v*h[7]
		det=a*d-b*c
		if abs(det)<1e-10: raise ValueError('degenerate homography at image horizon')
		e,f=u-h[2],v-h[5]
		x,y=(e*d-b*f)/det,(a*f-e*c)/det
		return (ref.finite(x/pn[2]+pn[0]),ref.finite(y/pn[2]+pn[1]))
	convert.report=dict(rms_px=rms,max_px=maximum,tolerance_px=tolerance,anchors=len(image))
	return convert

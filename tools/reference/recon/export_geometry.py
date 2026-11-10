"""Blender-only conversion of reconstructed USDZ to geometry-only OBJ."""
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parent))
from render_views import import_geometry
import bpy

args=sys.argv[sys.argv.index('--')+1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
_,vertices,faces=import_geometry(Path(args[0]))
Path(args[1]).write_text(''.join('v '+' '.join(format(v,'.17g') for v in p)+'\n' for p in vertices)+''.join('f '+' '.join(str(i+1) for i in face)+'\n' for face in faces))

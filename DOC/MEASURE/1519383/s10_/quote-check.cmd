@echo off
python -c "import sys; print(repr(sys.argv[1]))" "-e unity '-m3d_autorun -m3d_quit -m3d_force 1 -m3d_render 0 -m3d_warmup 60 -m3d_measure 30 -m3d_tag baseB'"

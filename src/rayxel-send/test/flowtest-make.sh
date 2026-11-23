#!/bin/sh
# Run on the Deck: flowtest-make.sh PICTURE [PX_SIDEWAYS [PX_DOWN]]
#
# Renders /tmp/flowtest.mp4, the picture Rayxel's motion estimation is checked against:
# everything in it moves by a known amount. 320 frames at 60 fps, 1280x800.
#   - PICTURE (any large photograph or artwork) scrolling by the given pixels a frame,
#     4 sideways unless said otherwise, with a flat grey box painted on it - so the box
#     moves with it and has nothing inside to match
#   - a HUD that stays where it is: a flat dark box with a few flat bars in it
#   - a flat strip that stays where it is, with a small square crossing it 4 px a frame
# flowtest-show.sh puts it on the screen, and flowtest-measure.py compares what
# `Rayxel --flow=only --shot=...` then wrote with what is true.
picture=$1; h=${2:-4}; v=${3:-0}
hs=$(awk "BEGIN{print $h/1280}"); vs=$(awk "BEGIN{print $v/800}")
ffmpeg -hide_banner -loglevel error -y -loop 1 -framerate 60 -i "$picture" -filter_complex "
[0:v]scale=-2:800,crop=1280:800,format=yuv420p,
     drawbox=x=140:y=230:w=280:h=220:color=0x8a8a8a:t=fill,
     scroll=h=$hs:v=$vs[moving];
color=c=0x303840:s=1280x140:r=60[panel];
color=c=0xe8e8e8:s=40x40:r=60[sprite];
[moving]drawbox=x=880:y=60:w=340:h=150:color=0x202428:t=fill,
        drawbox=x=900:y=80:w=120:h=20:color=0xd0d0d0:t=fill,
        drawbox=x=900:y=120:w=200:h=20:color=0x40c040:t=fill,
        drawbox=x=900:y=160:w=60:h=30:color=0xc04040:t=fill[withhud];
[withhud][panel]overlay=x=0:y=540[withpanel];
[withpanel][sprite]overlay=x='1240-mod(n*4,1280)':y=590:eval=frame[out]" \
  -map "[out]" -frames:v 320 -r 60 -c:v libx264 -preset veryfast -crf 10 -pix_fmt yuv420p -g 60 /tmp/flowtest.mp4 \
  && ls -la /tmp/flowtest.mp4

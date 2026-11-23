#!/bin/sh
# Run on the Deck. Shows something moving for $2 seconds, focused by gamescope like a game:
#   flowtest-show.sh cube 60      the spinning Vulkan cube
#   flowtest-show.sh test 60      /tmp/flowtest.mp4, the picture with known motion
what=${1:-cube}; seconds=${2:-45}
systemctl --user stop rayxel-show 2>/dev/null
if [ "$what" = test ]; then
	systemd-run --user --unit=rayxel-show --collect -E DISPLAY=:1 -E SDL_VIDEODRIVER=x11 /usr/bin/timeout "$seconds" \
		/usr/bin/ffplay -loop 0 -an -noborder -x 1280 -y 800 -loglevel error /tmp/flowtest.mp4 >/dev/null 2>&1
	match=flowtest
else
	systemd-run --user --unit=rayxel-show --collect -E DISPLAY=:1 /usr/bin/timeout "$seconds" \
		/usr/bin/vkcube --width 1280 --height 800 >/dev/null 2>&1
	match=vkcube
fi
appid=$(DISPLAY=:0 xprop -root GAMESCOPECTRL_BASELAYER_APPID | sed 's/.*= //; s/,.*//')
for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
	wid=$(DISPLAY=:1 xwininfo -root -tree 2>/dev/null | grep -i "$match" | head -1 | awk '{print $1}')
	[ -n "$wid" ] && break
	sleep 0.3
done
[ -z "$wid" ] && { echo "show: no window"; exit 1; }
DISPLAY=:1 xprop -id "$wid" -f STEAM_GAME 32c -set STEAM_GAME "$appid"
sleep 0.7
echo "show: $what, window $wid as app $appid, focused: $(DISPLAY=:0 xprop -root GAMESCOPE_FOCUSED_APP | sed 's/.*= //')"

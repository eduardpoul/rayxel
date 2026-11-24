#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include <pipewire/pipewire.h>

#include "common.h"
#include "encoder.h"
#include "probe.h"
#include "record.h"
#include "server.h"
#include "wire.h"

static void usage(void)
{
	printf("rayxel-send %s - sends a Steam Deck's picture to Rayxel\n"
	       "\n"
	       "  rayxel-send --listen[=PORT] [--allow-input] [--buffer=FRAMES] [--max-frame=X] [--gop=N]\n"
	       "                              [--source=NAME] [--frames-file=FILE --frames-size=WxH]\n"
	       "  rayxel-send --probe [--size=WxH] [--seconds=N] [--no-capture]\n"
	       "  rayxel-send --record=FILE [--codec=NAME] [--size=WxH] [--seconds=N] [--bitrate=MBIT]\n"
	       "                            [--fps=N] [--buffer=FRAMES] [--max-frame=X] [--gop=N]\n"
	       "                            [--source=NAME]\n"
	       "\n"
	       "  --listen[=PORT] wait for Rayxel to ask for the picture and stream it there; the\n"
	       "                  viewer chooses the codec, size and bitrate (default port %d)\n"
	       "  --probe         report what this machine offers: the gamescope video source and\n"
	       "                  its formats, the encoders, and a short trial capture\n"
	       "  --record=FILE   capture, encode and write the stream to FILE, then report\n"
	       "                  what each stage cost; FILE may be - to measure without writing\n"
	       "\n"
	       "  --allow-input   --listen: let the viewer play with its own gamepad, keyboard and\n"
	       "                  mouse, which appear here as devices. Off unless asked for: it\n"
	       "                  lets whoever is watching type and click on this machine\n"
	       "  --frames-file=FILE --listen: send these pictures in a loop instead of capturing:\n"
	       "                  raw NV12, one after another, BT.709 limited range; for tests\n"
	       "  --frames-size=WxH the size of each picture in --frames-file\n"
	       "  --codec=NAME    --record: hevc (default), av1 or pyrowave\n"
	       "  --size=WxH      size to ask gamescope for (default: the output's own; gamescope\n"
	       "                  only scales down)\n"
	       "  --seconds=N     how long to run (default 3 for --probe, 5 for --record)\n"
	       "  --no-capture    --probe: skip the trial capture\n"
	       "  --bitrate=MBIT  target bitrate in Mbit/s (default 20 for HEVC, 15 for AV1,\n"
	       "                  140 for PyroWave)\n"
	       "  --fps=N         the rate the bitrate is budgeted for (default 60); frames are\n"
	       "                  encoded as they arrive whatever this says\n"
	       "  --buffer=FRAMES how many frames' worth of bits the rate control may borrow\n"
	       "                  (default 2); smaller keeps frame sizes more even\n"
	       "  --max-frame=X   cap any one frame at X times the average frame (default: no cap)\n"
	       "  --gop=N         a keyframe every N frames (default: only the first, and in\n"
	       "                  --listen whenever the viewer asks for one)\n"
	       "  --source=NAME   node name of the video source (default gamescope)\n"
	       "  --version       print the version\n",
	       RAYXEL_SEND_VERSION, WIRE_PORT);
}

static bool parse_size(const char *text, uint32_t *width, uint32_t *height)
{
	unsigned w = 0, h = 0;
	char tail = 0;
	if (sscanf(text, "%ux%u%c", &w, &h, &tail) != 2) return false;
	if (w < 16 || h < 16 || w > 16384 || h > 16384) return false;
	*width = w;
	*height = h;
	return true;
}

static bool parse_codec(const char *text, uint8_t *codec)
{
	if (strcmp(text, "hevc") == 0) *codec = WIRE_CODEC_HEVC;
	else if (strcmp(text, "av1") == 0) *codec = WIRE_CODEC_AV1;
	else if (strcmp(text, "pyrowave") == 0) *codec = WIRE_CODEC_PYROWAVE;
	else return false;
	return true;
}

static bool parse_number(const char *text, double min, double max, double *value)
{
	char *end = NULL;
	double v = strtod(text, &end);
	if (end == text || *end != '\0' || v < min || v > max) return false;
	*value = v;
	return true;
}

static int run_probe(uint32_t width, uint32_t height, int seconds, bool capture)
{
	struct pw_target target;
	struct va_summary va;
	struct trial_summary trial = { 0 };

	printf("rayxel-send %s probe\n", RAYXEL_SEND_VERSION);
	char why[256];

	probe_system();
	bool have_source = probe_pipewire(&target) && target.found;
	probe_va(&va);
	if (capture && have_source) probe_capture(&target, width, height, seconds, &trial);

	section("summary");
	if (!have_source) printf("video source : not found\n");
	else printf("video source : %s, node %u\n", target.is_gamescope ? "gamescope" : target.name, target.node_id);

	if (va.hevc_main)
		printf("HEVC encoder : yes%s, up to %ux%u, rate control %s, packed headers %s\n",
		       va.hevc_main10 ? " (8 and 10 bit)" : " (8 bit)", va.max_width, va.max_height, va.rate_control,
		       va.packed_headers);
	else
		printf("HEVC encoder : not found\n");

	why[0] = '\0';
	if (encoder_available(WIRE_CODEC_AV1, why, sizeof(why))) printf("AV1 encoder  : yes\n");
	else printf("AV1 encoder  : no, %s\n", why);
	why[0] = '\0';
	if (encoder_available(WIRE_CODEC_PYROWAVE, why, sizeof(why))) printf("PyroWave     : yes\n");
	else printf("PyroWave     : no, %s\n", why);

	if (!capture) printf("trial capture: skipped\n");
	else if (!have_source) printf("trial capture: not run, there is no source\n");
	else if (trial.frames > 0)
		printf("trial capture: %d frames of %ux%u at %.1f fps\n", trial.frames, trial.width, trial.height, trial.fps);
	else printf("trial capture: no frames\n");

	bool usable = have_source && va.hevc_main && (!capture || trial.frames > 0);
	return usable ? 0 : 1;
}

int main(int argc, char **argv)
{
	bool probe = false, capture = true, listen = false, allow_input = false;
	const char *record = NULL, *source = "gamescope", *frames_file = NULL;
	uint32_t width = 0, height = 0, frames_width = 0, frames_height = 0;
	uint8_t codec = WIRE_CODEC_HEVC;
	double seconds = 0, bitrate = 0, fps = 60, buffer = 2, gop = 0, max_frame = 0, port = WIRE_PORT;

	/* The report is read through ssh or a pipe as often as on a terminal; without this
	 * it would arrive after everything the libraries write to stderr. */
	setvbuf(stdout, NULL, _IOLBF, 0);

	for (int i = 1; i < argc; i++) {
		const char *arg = argv[i];
		bool ok = true;

		if (strcmp(arg, "--probe") == 0) probe = true;
		else if (strcmp(arg, "--no-capture") == 0) capture = false;
		else if (strcmp(arg, "--allow-input") == 0) allow_input = true;
		else if (strcmp(arg, "--listen") == 0) listen = true;
		else if (strncmp(arg, "--listen=", 9) == 0) {
			listen = true;
			ok = parse_number(arg + 9, 1, 65535, &port);
		} else if (strncmp(arg, "--record=", 9) == 0) record = arg + 9;
		else if (strncmp(arg, "--source=", 9) == 0) source = arg + 9;
		else if (strncmp(arg, "--size=", 7) == 0) ok = parse_size(arg + 7, &width, &height);
		else if (strncmp(arg, "--codec=", 8) == 0) ok = parse_codec(arg + 8, &codec);
		else if (strncmp(arg, "--frames-file=", 14) == 0) frames_file = arg + 14;
		else if (strncmp(arg, "--frames-size=", 14) == 0) ok = parse_size(arg + 14, &frames_width, &frames_height);
		else if (strncmp(arg, "--seconds=", 10) == 0) ok = parse_number(arg + 10, 1, 3600, &seconds);
		else if (strncmp(arg, "--bitrate=", 10) == 0) ok = parse_number(arg + 10, 0.2, 500, &bitrate);
		else if (strncmp(arg, "--fps=", 6) == 0) ok = parse_number(arg + 6, 1, 240, &fps);
		else if (strncmp(arg, "--buffer=", 9) == 0) ok = parse_number(arg + 9, 0.5, 600, &buffer);
		else if (strncmp(arg, "--max-frame=", 12) == 0) ok = parse_number(arg + 12, 0, 1000, &max_frame);
		else if (strncmp(arg, "--gop=", 6) == 0) ok = parse_number(arg + 6, 0, 100000, &gop);
		else if (strcmp(arg, "--version") == 0) {
			printf("%s\n", RAYXEL_SEND_VERSION);
			return 0;
		} else if (strcmp(arg, "--help") == 0 || strcmp(arg, "-h") == 0) {
			usage();
			return 0;
		} else {
			fprintf(stderr, "unknown argument: %s\n\n", arg);
			usage();
			return 2;
		}

		if (!ok) {
			fprintf(stderr, "%s: not a value this option takes; see --help\n", arg);
			return 2;
		}
	}

	int modes = probe + listen + (record != NULL);
	if (modes != 1) {
		if (modes > 1) fprintf(stderr, "--listen, --probe and --record are separate modes; pick one\n\n");
		usage();
		return 2;
	}
	if ((record && record[0] == '\0') || source[0] == '\0' || (frames_file && frames_file[0] == '\0')) {
		fprintf(stderr, "--record, --source and --frames-file need a value\n");
		return 2;
	}
	if (frames_file && (!listen || frames_width == 0)) {
		fprintf(stderr, "--frames-file goes with --listen and needs --frames-size\n");
		return 2;
	}
	if (frames_width && (frames_width % 2 || frames_height % 2)) {
		fprintf(stderr, "--frames-size: NV12 needs an even width and height\n");
		return 2;
	}
	if (bitrate == 0) bitrate = codec == WIRE_CODEC_PYROWAVE ? 140 : codec == WIRE_CODEC_AV1 ? 15 : 20;

	/* A plain ssh command line does not always carry the session's runtime directory,
	 * and without it the PipeWire socket cannot be found. */
	if (!getenv("XDG_RUNTIME_DIR")) {
		char path[64];
		snprintf(path, sizeof(path), "/run/user/%u", (unsigned)getuid());
		setenv("XDG_RUNTIME_DIR", path, 0);
	}

	pw_init(&argc, &argv);
	int status;
	if (probe) {
		status = run_probe(width, height, seconds > 0 ? (int)seconds : 3, capture);
	} else if (listen) {
		struct server_options options = {
			.port = (uint16_t)port,
			.source = source,
			.buffer_frames = buffer,
			.max_frame = max_frame,
			.gop = (uint32_t)gop,
			.allow_input = allow_input,
			.frames_file = frames_file,
			.frames_width = frames_width,
			.frames_height = frames_height,
		};
		printf("rayxel-send %s\n", RAYXEL_SEND_VERSION);
		status = server_run(&options);
	} else {
		struct record_options options = {
			.path = strcmp(record, "-") == 0 ? NULL : record,
			.codec = codec,
			.source = source,
			.width = width,
			.height = height,
			.fps = (uint32_t)(fps + 0.5),
			.bitrate_kbps = (uint32_t)(bitrate * 1000 + 0.5),
			.buffer_frames = buffer,
			.max_frame = max_frame,
			.gop = (uint32_t)gop,
			.seconds = seconds > 0 ? (int)seconds : 5,
		};
		printf("rayxel-send %s\n", RAYXEL_SEND_VERSION);
		status = record_run(&options);
	}
	pw_deinit();
	return status;
}

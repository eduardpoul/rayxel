/*
 * Writes the AV1 headers rayxel-send would hand the encoder - a temporal delimiter, the
 * sequence header and a key frame's header, then inter frames' - into an OBU file, for
 * FFmpeg's AV1 parser to read back field by field:
 *
 *   av1-headers-test WIDTH HEIGHT FILE
 *   ffmpeg -f obu -i FILE -c copy -bsf:v trace_headers -f null -
 *
 * No Deck has an AV1 encoder, so this is how the headers are checked at all. There is no
 * picture in the file - no tile data - only headers, which is all the parser needs.
 */
#include <stdio.h>
#include <stdlib.h>

#include "../src/av1_headers.h"

int main(int argc, char **argv)
{
	if (argc != 4) {
		fprintf(stderr, "usage: %s WIDTH HEIGHT FILE\n", argv[0]);
		return 2;
	}
	uint32_t width = (uint32_t)atoi(argv[1]), height = (uint32_t)atoi(argv[2]);
	FILE *file = fopen(argv[3], "wb");
	if (!file) return 1;

	struct av1_stream s = { 0 };
	av1_stream_init(&s, width, height, 60, 15000);
	s.colour_primaries = s.transfer_characteristics = s.matrix_coefficients = 1; /* BT.709 */
	s.tx_mode_select = true;
	/* As va_encoder.c does: the size said apart when the hardware pads to 64x16 blocks. */
	s.render_size = width % 64 || height % 16;

	uint8_t buffer[AV1_HEADERS_MAX];
	size_t n;
	for (int i = 0; i < 4; i++) {
		struct av1_frame f = {
			.keyframe = i == 0,
			.order_hint = (uint8_t)i,
			.refresh_frame_flags = i == 0 ? 0xff : (uint8_t)(1u << (i & 1)),
			.ref_slot = (uint8_t)((i + 1) & 1),
			.base_q_idx = 128,
		};
		n = av1_write_temporal_delimiter(buffer);
		fwrite(buffer, 1, n, file);
		if (f.keyframe) {
			n = av1_write_sequence_header(&s, buffer);
			fwrite(buffer, 1, n, file);
		}
		n = av1_write_frame_header(&s, &f, buffer);
		fwrite(buffer, 1, n, file);
		printf("frame %d: %zu bytes, %u bits; qindex at bit %u, loop filter at %u, cdef at %u (%u bits)\n", i, n,
		       f.size_in_bits, f.bit_offset_qindex, f.bit_offset_loopfilter, f.bit_offset_cdef, f.size_in_bits_cdef);
	}
	fclose(file);
	return 0;
}

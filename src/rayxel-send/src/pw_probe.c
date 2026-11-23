#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <pipewire/pipewire.h>
#include <spa/param/param.h>
#include <spa/utils/result.h>

#include "common.h"
#include "pod_print.h"
#include "probe.h"

/* Listeners keep pointers into this array, so it is allocated once and never grown. */
#define MAX_GLOBALS 1024

struct probe;

struct global {
	struct probe *probe;
	uint32_t id;
	bool is_port;
	uint32_t node_id; /* ports only: the node the port belongs to */
	char name[128];
	char media_class[64];
	char serial[32];
	char direction[8];
	struct pw_proxy *proxy;
	struct spa_hook listener;
	int formats;
};

struct probe {
	struct pw_main_loop *loop;
	struct pw_context *context;
	struct pw_core *core;
	struct pw_registry *registry;
	struct spa_hook core_listener;
	struct spa_hook registry_listener;
	struct spa_source *timer;
	int pending;
	bool timed_out;
	bool failed;
	char server[96];
	struct global *globals;
	int count;
};

static void copy(char *out, size_t cap, const char *text)
{
	snprintf(out, cap, "%s", text ? text : "");
}

static void on_core_info(void *data, const struct pw_core_info *info)
{
	struct probe *p = data;
	snprintf(p->server, sizeof(p->server), "%s %s", info->name, info->version);
}

static void on_core_done(void *data, uint32_t id, int seq)
{
	struct probe *p = data;
	if (id == PW_ID_CORE && seq == p->pending) pw_main_loop_quit(p->loop);
}

static void on_core_error(void *data, uint32_t id, int seq, int res, const char *message)
{
	struct probe *p = data;
	fprintf(stderr, "pipewire: error on object %u: %s (%s)\n", id, message, spa_strerror(res));
	if (id == PW_ID_CORE && res == -EPIPE) {
		p->failed = true;
		pw_main_loop_quit(p->loop);
	}
}

static const struct pw_core_events core_events = {
	PW_VERSION_CORE_EVENTS,
	.info = on_core_info,
	.done = on_core_done,
	.error = on_core_error,
};

static void on_timeout(void *data, uint64_t expirations)
{
	struct probe *p = data;
	p->timed_out = true;
	pw_main_loop_quit(p->loop);
}

/*
 * Runs the loop until the server has answered everything sent so far. PipeWire is
 * asynchronous throughout; a sync is how a client learns that a batch of replies is
 * complete. Bounded, because a probe that hangs tells nobody anything.
 */
static bool roundtrip(struct probe *p)
{
	struct pw_loop *loop = pw_main_loop_get_loop(p->loop);
	struct timespec limit = { .tv_sec = 3 };

	p->timed_out = false;
	p->pending = pw_core_sync(p->core, PW_ID_CORE, 0);
	pw_loop_update_timer(loop, p->timer, &limit, NULL, false);
	pw_main_loop_run(p->loop);
	pw_loop_update_timer(loop, p->timer, NULL, NULL, false);

	if (p->timed_out) fprintf(stderr, "pipewire: the server did not answer within 3 s\n");
	return !p->timed_out && !p->failed;
}

static void on_global(void *data, uint32_t id, uint32_t permissions, const char *type, uint32_t version,
		      const struct spa_dict *props)
{
	struct probe *p = data;
	bool is_node = strcmp(type, PW_TYPE_INTERFACE_Node) == 0;
	bool is_port = strcmp(type, PW_TYPE_INTERFACE_Port) == 0;

	if ((!is_node && !is_port) || props == NULL || p->count >= MAX_GLOBALS) return;

	struct global *g = &p->globals[p->count++];
	g->probe = p;
	g->id = id;
	g->is_port = is_port;
	g->node_id = SPA_ID_INVALID;
	copy(g->name, sizeof(g->name), spa_dict_lookup(props, is_port ? PW_KEY_PORT_NAME : PW_KEY_NODE_NAME));
	copy(g->media_class, sizeof(g->media_class), spa_dict_lookup(props, PW_KEY_MEDIA_CLASS));
	copy(g->serial, sizeof(g->serial), spa_dict_lookup(props, PW_KEY_OBJECT_SERIAL));
	if (is_port) {
		const char *node = spa_dict_lookup(props, PW_KEY_NODE_ID);
		if (node) g->node_id = (uint32_t)strtoul(node, NULL, 10);
		copy(g->direction, sizeof(g->direction), spa_dict_lookup(props, PW_KEY_PORT_DIRECTION));
	}
}

static const struct pw_registry_events registry_events = {
	PW_VERSION_REGISTRY_EVENTS,
	.global = on_global,
};

static void on_param(void *data, int seq, uint32_t id, uint32_t index, uint32_t next, const struct spa_pod *param)
{
	struct global *g = data;
	if (id != SPA_PARAM_EnumFormat || param == NULL) return;
	printf("  format %d (%s %u):\n", ++g->formats, g->is_port ? "port" : "node", g->id);
	format_describe(param, "    ");
}

static const struct pw_node_events node_events = {
	PW_VERSION_NODE_EVENTS,
	.param = on_param,
};

static const struct pw_port_events port_events = {
	PW_VERSION_PORT_EVENTS,
	.param = on_param,
};

static void query_formats(struct probe *p, struct global *g)
{
	if (g->proxy) return;

	if (g->is_port) {
		g->proxy = pw_registry_bind(p->registry, g->id, PW_TYPE_INTERFACE_Port, PW_VERSION_PORT, 0);
		if (!g->proxy) return;
		pw_port_add_listener((struct pw_port *)g->proxy, &g->listener, &port_events, g);
		pw_port_enum_params((struct pw_port *)g->proxy, 0, SPA_PARAM_EnumFormat, 0, UINT32_MAX, NULL);
	} else {
		g->proxy = pw_registry_bind(p->registry, g->id, PW_TYPE_INTERFACE_Node, PW_VERSION_NODE, 0);
		if (!g->proxy) return;
		pw_node_add_listener((struct pw_node *)g->proxy, &g->listener, &node_events, g);
		pw_node_enum_params((struct pw_node *)g->proxy, 0, SPA_PARAM_EnumFormat, 0, UINT32_MAX, NULL);
	}
}

static bool is_video_source(const struct global *g)
{
	return !g->is_port && strcmp(g->media_class, "Video/Source") == 0;
}

bool probe_pipewire(struct pw_target *target)
{
	struct probe p = { 0 };
	bool ok = false;

	memset(target, 0, sizeof(*target));
	section("pipewire");

	p.globals = calloc(MAX_GLOBALS, sizeof(*p.globals));
	p.loop = pw_main_loop_new(NULL);
	if (!p.globals || !p.loop) {
		printf("cannot create a PipeWire loop\n");
		goto out;
	}
	p.context = pw_context_new(pw_main_loop_get_loop(p.loop), NULL, 0);
	p.core = p.context ? pw_context_connect(p.context, NULL, 0) : NULL;
	if (!p.core) {
		printf("cannot connect to PipeWire: %s\n", strerror(errno));
		printf("  run this as the logged-in user (deck), with the session running;\n");
		printf("  the socket is expected at $XDG_RUNTIME_DIR/pipewire-0\n");
		goto out;
	}

	p.timer = pw_loop_add_timer(pw_main_loop_get_loop(p.loop), on_timeout, &p);
	pw_core_add_listener(p.core, &p.core_listener, &core_events, &p);
	p.registry = pw_core_get_registry(p.core, PW_VERSION_REGISTRY, 0);
	pw_registry_add_listener(p.registry, &p.registry_listener, &registry_events, &p);

	if (!roundtrip(&p)) goto out;

	int nodes = 0, video = 0;
	for (int i = 0; i < p.count; i++) {
		if (p.globals[i].is_port) continue;
		nodes++;
		if (strncmp(p.globals[i].media_class, "Video/", 6) == 0) video++;
	}
	printf("server   : %s (client library %s)\n", p.server[0] ? p.server : "unknown", pw_get_library_version());
	printf("nodes    : %d, of which %d video\n", nodes, video);

	for (int i = 0; i < p.count; i++) {
		const struct global *g = &p.globals[i];
		if (g->is_port || strncmp(g->media_class, "Video/", 6) != 0) continue;
		printf("  node %-4u %-14s '%s'%s%s\n", g->id, g->media_class, g->name, g->serial[0] ? ", serial " : "",
		       g->serial);
	}

	/* gamescope by name if it is there - exactly, then loosely, because the name is
	 * whatever its binary happens to be called; otherwise any video source, so the rest
	 * of the probe still has something to say on a machine that is not a Deck. */
	struct global *chosen = NULL;
	for (int i = 0; i < p.count && !chosen; i++)
		if (is_video_source(&p.globals[i]) && strcmp(p.globals[i].name, "gamescope") == 0) chosen = &p.globals[i];
	for (int i = 0; i < p.count && !chosen; i++)
		if (is_video_source(&p.globals[i]) && strstr(p.globals[i].name, "gamescope")) chosen = &p.globals[i];
	for (int i = 0; i < p.count && !chosen; i++)
		if (is_video_source(&p.globals[i])) chosen = &p.globals[i];

	if (!chosen) {
		printf("no video source - gamescope publishes one only while Game Mode is running\n");
		goto out;
	}

	target->found = true;
	target->is_gamescope = strstr(chosen->name, "gamescope") != NULL;
	target->node_id = chosen->id;
	copy(target->serial, sizeof(target->serial), chosen->serial);
	copy(target->name, sizeof(target->name), chosen->name);

	printf("source   : node %u '%s'%s\n", chosen->id, chosen->name,
	       target->is_gamescope ? "" : " (not gamescope - used as a stand-in)");
	printf("offers:\n");

	/* A stream publishes its formats on the node in current PipeWire and only on its
	 * port in older ones, so the port is asked when the node has nothing to say. */
	query_formats(&p, chosen);
	if (!roundtrip(&p)) goto out;
	if (chosen->formats == 0) {
		for (int i = 0; i < p.count; i++)
			if (p.globals[i].is_port && p.globals[i].node_id == chosen->id) query_formats(&p, &p.globals[i]);
		if (!roundtrip(&p)) goto out;
	}

	int formats = chosen->formats;
	for (int i = 0; i < p.count; i++)
		if (p.globals[i].is_port && p.globals[i].node_id == chosen->id) formats += p.globals[i].formats;
	if (formats == 0) printf("  (the node lists no formats; the trial capture below will show what it accepts)\n");

	ok = true;

out:
	for (int i = 0; i < p.count; i++) {
		if (!p.globals[i].proxy) continue;
		spa_hook_remove(&p.globals[i].listener);
		pw_proxy_destroy(p.globals[i].proxy);
	}
	if (p.registry) {
		spa_hook_remove(&p.registry_listener);
		pw_proxy_destroy((struct pw_proxy *)p.registry);
	}
	if (p.core) {
		spa_hook_remove(&p.core_listener);
		if (p.timer) pw_loop_destroy_source(pw_main_loop_get_loop(p.loop), p.timer);
		pw_core_disconnect(p.core);
	}
	if (p.context) pw_context_destroy(p.context);
	if (p.loop) pw_main_loop_destroy(p.loop);
	free(p.globals);
	return ok;
}

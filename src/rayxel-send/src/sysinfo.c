#include <ctype.h>
#include <dirent.h>
#include <gnu/libc-version.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/utsname.h>
#include <unistd.h>

#include "common.h"
#include "probe.h"

static bool read_line(const char *path, char *out, size_t cap)
{
	FILE *f = fopen(path, "r");
	if (!f) return false;
	bool ok = fgets(out, (int)cap, f) != NULL;
	fclose(f);
	if (ok) out[strcspn(out, "\r\n")] = '\0';
	return ok;
}

/* One KEY=value or KEY="value" line of /etc/os-release. */
static bool os_release(const char *key, char *out, size_t cap)
{
	FILE *f = fopen("/etc/os-release", "r");
	if (!f) return false;

	char line[256];
	size_t key_len = strlen(key);
	bool found = false;
	while (fgets(line, sizeof(line), f)) {
		if (strncmp(line, key, key_len) != 0 || line[key_len] != '=') continue;
		char *value = line + key_len + 1;
		value[strcspn(value, "\r\n")] = '\0';
		size_t len = strlen(value);
		if (len >= 2 && value[0] == '"' && value[len - 1] == '"') {
			value[len - 1] = '\0';
			value++;
		}
		snprintf(out, cap, "%s", value);
		found = true;
		break;
	}
	fclose(f);
	return found;
}

static bool cpu_model(char *out, size_t cap)
{
	FILE *f = fopen("/proc/cpuinfo", "r");
	if (!f) return false;

	char line[256];
	bool found = false;
	while (fgets(line, sizeof(line), f)) {
		if (strncmp(line, "model name", 10) != 0) continue;
		char *colon = strchr(line, ':');
		if (!colon) continue;
		colon++;
		while (*colon == ' ') colon++;
		colon[strcspn(colon, "\r\n")] = '\0';
		snprintf(out, cap, "%s", colon);
		found = true;
		break;
	}
	fclose(f);
	return found;
}

/* The pid of the first process whose name starts with `prefix`, or 0. */
static int find_process(const char *prefix)
{
	DIR *proc = opendir("/proc");
	if (!proc) return 0;

	int pid = 0;
	struct dirent *entry;
	while ((entry = readdir(proc)) != NULL) {
		if (!isdigit((unsigned char)entry->d_name[0])) continue;
		char path[300], comm[64];
		snprintf(path, sizeof(path), "/proc/%s/comm", entry->d_name);
		if (!read_line(path, comm, sizeof(comm))) continue;
		if (strncmp(comm, prefix, strlen(prefix)) == 0) {
			pid = atoi(entry->d_name);
			break;
		}
	}
	closedir(proc);
	return pid;
}

void probe_system(void)
{
	char text[256];
	struct utsname uts;

	section("system");

	if (read_line("/sys/class/dmi/id/product_name", text, sizeof(text))) {
		/* Valve's board names: the LCD model is Jupiter, the OLED one Galileo. */
		const char *model = strcmp(text, "Jupiter") == 0 ? " (Steam Deck LCD)"
				  : strcmp(text, "Galileo") == 0 ? " (Steam Deck OLED)" : "";
		printf("device   : %s%s\n", text, model);
	}
	if (os_release("PRETTY_NAME", text, sizeof(text))) {
		char build[64] = "";
		os_release("BUILD_ID", build, sizeof(build));
		printf("system   : %s%s%s\n", text, build[0] ? ", build " : "", build);
	}
	if (uname(&uts) == 0) printf("kernel   : %s\n", uts.release);
	if (cpu_model(text, sizeof(text))) printf("cpu      : %s\n", text);
	printf("glibc    : %s\n", gnu_get_libc_version());
	printf("user     : uid %u, XDG_RUNTIME_DIR=%s\n", (unsigned)getuid(),
	       getenv("XDG_RUNTIME_DIR") ? getenv("XDG_RUNTIME_DIR") : "(unset)");

	int pid = find_process("gamescope");
	if (pid) printf("gamescope: running, pid %d\n", pid);
	else printf("gamescope: not running - the picture is only available in Game Mode\n");
}

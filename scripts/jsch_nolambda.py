#!/usr/bin/env python3
"""Builds tools/jsch-0.2.20-nolambda.jar for build_linux.py's dx fallback.

dx (Debian/Ubuntu package dalvik-exchange) can't desugar Java 8 lambdas, and JSch 0.2.x uses a few. This takes
a checkout of JSch 0.2.20's source (git clone --depth 1 --branch jsch-0.2.20 https://github.com/mwiede/jsch),
leaves out the optional classes that need other libraries (Bouncy Castle, JNA, junixsocket, Log4j, SLF4J, the
SSH agent), rewrites the remaining lambdas and method references as anonymous classes (same behavior), and
compiles it for Java 8.

usage: jsch_nolambda.py <jsch source checkout> <output jar>
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

if len(sys.argv) != 3:
    sys.exit(__doc__)
checkout, out_jar = (os.path.abspath(p) for p in sys.argv[1:3])
work = tempfile.mkdtemp(prefix="jsch-nolambda-")
src = os.path.join(work, "java")
shutil.copytree(os.path.join(checkout, "src", "main", "java"), src)
pkg = os.path.join(src, "com", "jcraft", "jsch")

# Optional classes that need other libraries (JSch loads them by name only if configured).
shutil.rmtree(os.path.join(pkg, "bc"))
for name in ["PageantConnector", "JUnixSocketFactory", "Log4j2Logger", "Slf4jLogger", "SSHAgentConnector"]:
    os.remove(os.path.join(pkg, name + ".java"))
# Maven fills ${project.version} into these.
templates = os.path.join(checkout, "src", "main", "java-templates", "com", "jcraft", "jsch")
for name in os.listdir(templates):
    with open(os.path.join(templates, name), encoding="utf-8") as f:
        text = f.read().replace("${project.version}", "0.2.20")
    with open(os.path.join(pkg, name), "w", encoding="utf-8") as f:
        f.write(text)


def edit(rel, fn):
    p = os.path.join(pkg, rel)
    with open(p, encoding="utf-8") as f:
        s = f.read()
    n = fn(s)
    if n == s:
        sys.exit("nothing to rewrite in " + rel + " (not JSch 0.2.20?)")
    with open(p, "w", encoding="utf-8") as f:
        f.write(n)


def runnable(cls):
    return ("\n  static Runnable __r(final %s c) {\n    return new Runnable() {\n      @Override\n"
            "      public void run() {\n        c.run();\n      }\n    };\n  }\n" % cls)


def add_helpers(s, cls, types):
    i = s.index("{", s.index("class " + cls)) + 1
    return s[:i] + "".join(runnable(t) for t in types) + s[i:]


# x::run (a Runnable) -> __r(x), an equivalent anonymous Runnable.
edit("Channel.java", lambda s: add_helpers(s, "Channel", ["Channel"]))
for c in ["ChannelShell", "ChannelExec", "ChannelSubsystem", "ChannelDirectTCPIP"]:
    edit(c + ".java", lambda s: s.replace("this::run", "__r(this)"))
edit("PortWatcher.java", lambda s: add_helpers(s.replace("this::run", "__r(this)"), "PortWatcher", ["PortWatcher"]))
edit("Session.java", lambda s: add_helpers(re.sub(r"\b(this|channel|pw)::run", r"__r(\1)", s),
                                           "Session", ["Session", "PortWatcher", "Channel"]))
# () -> "message" (a Supplier<String>) -> an anonymous Supplier.
supplier = lambda s: re.sub(r"\(\) -> (.*?)\);", r"new Supplier<String>() { @Override public String get() { return \1; } });", s)
edit(os.path.join("juz", "Compression.java"), supplier)
edit(os.path.join("jzlib", "Compression.java"), supplier)
edit("OpenSSHConfig.java", lambda s: s.replace(
    ".map(string -> string.toUpperCase(Locale.ROOT))",
    ".map(new java.util.function.Function<String, String>() { @Override public String apply(String string) "
    "{ return string.toUpperCase(Locale.ROOT); } })"))

classes = os.path.join(work, "classes")
sources = [os.path.join(d, f) for d, _, fs in os.walk(src) for f in fs if f.endswith(".java")]
subprocess.run(["javac", "--release", "8", "-nowarn", "-encoding", "UTF-8", "-d", classes] + sources, check=True)
# Fail if any lambda (invokedynamic) is left.
for d, _, fs in os.walk(classes):
    for f in fs:
        dump = subprocess.run(["javap", "-c", "-p", os.path.join(d, f)], check=True, capture_output=True, text=True).stdout
        if "invokedynamic" in dump:
            sys.exit("lambda left in " + os.path.join(d, f))
if os.path.exists(out_jar):
    os.remove(out_jar)
subprocess.run(["jar", "cf", out_jar, "-C", classes, "."], check=True)
shutil.rmtree(work)
print("Wrote " + out_jar)

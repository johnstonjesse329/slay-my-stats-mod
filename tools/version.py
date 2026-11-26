"""
tools/version.py — Give every pushed mod change its own version.

    python tools/version.py push <branch>             # test build: v0.0.7-test.N
    python tools/version.py push <branch> --release   # release: v0.0.7

`push` writes the next free version into SlayMyStats.json, commits it, tags the commit
with the version and pushes branch and tag together. N is one counter for all branches:
the remote's tags say which numbers are taken. A commit that already carries its version
is pushed as it is.

githooks/pre-push runs `hook`, which refuses a branch push that changes the mod without
that tag. Changes to docs, tools, hooks and the probe don't need a version.
"""
import argparse
import json
import re
import subprocess
import sys

MANIFEST = "SlayMyStats.json"
UNSHIPPED = ("tools/", "githooks/", "Probe/", ".git", "LICENSE")
ZERO = "0" * 40
RELEASE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")
TEST = re.compile(r"^v(\d+\.\d+\.\d+)-test\.(\d+)$")


def git(*args: str) -> str:
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True,
                          encoding="utf-8").stdout.strip()


def shipped(path: str) -> bool:
    return not (path.endswith(".md") or path.startswith(UNSHIPPED))


def remote_tags(remote: str) -> dict:
    """{tag: commit}; an annotated tag reads as the commit it points to."""
    tags = {}
    for line in git("ls-remote", "--tags", remote).splitlines():
        sha, ref = line.split("\t")
        name = ref.removeprefix("refs/tags/")
        if name.endswith("^{}"):
            tags[name[:-3]] = sha
        else:
            tags.setdefault(name, sha)
    return tags


def next_version(tags, release: bool) -> str:
    """The patch after the newest release, or that patch's next test number."""
    released = [tuple(map(int, m.groups())) for t in tags if (m := RELEASE.match(t))]
    major, minor, patch = max(released, default=(0, 0, 0))
    base = f"{major}.{minor}.{patch + 1}"
    if release:
        return f"v{base}"
    used = [int(m[2]) for t in tags if (m := TEST.match(t)) and m[1] == base]
    return f"v{base}-test.{max(used, default=0) + 1}"


def version_at(commit: str) -> str:
    return json.loads(git("show", f"{commit}:{MANIFEST}"))["version"]


def changed(old: str, new: str) -> list:
    return git("diff", "--name-only", "--no-renames", old, new).splitlines()


def needs_version(remote: str, local: str, old: str) -> bool:
    """Whether pushing local over old (ZERO: a new branch) sends a mod change."""
    if old == ZERO:
        # Nothing to compare with: only a commit the remote already has is exempt.
        return not git("branch", "-r", "--list", f"{remote}/*", "--contains", local)
    try:
        return any(map(shipped, changed(old, local)))
    except subprocess.CalledProcessError:
        return True  # the remote's tip isn't here, so it can't be compared


def check(remote: str, lines: list) -> list:
    """Problems with a push, from the lines git gives a pre-push hook."""
    refs = [line.split() for line in lines if line.strip()]
    pushed_tags = {r[2].removeprefix("refs/tags/"): r[1] for r in refs if r[2].startswith("refs/tags/")}
    problems, taken = [], None
    for _, local, ref, old in refs:
        if not ref.startswith("refs/heads/") or local == ZERO or not needs_version(remote, local, old):
            continue
        version = version_at(local)
        if taken is None:
            taken = remote_tags(remote)
        tagged = taken.get(version)
        if tagged is None and version in pushed_tags:
            tagged = git("rev-parse", f"{pushed_tags[version]}^{{commit}}")
        if tagged != local:
            why = "is already used by another commit" if tagged else "isn't tagged on this commit"
            problems.append(f"{ref.removeprefix('refs/heads/')}: version {version} {why}.")
    return problems


def push(remote: str, branch: str, release: bool) -> None:
    if git("status", "--porcelain", "--untracked-files=no"):
        sys.exit("Commit your changes first.")
    head = git("rev-parse", "HEAD")
    tags = remote_tags(remote)
    old = git("ls-remote", remote, f"refs/heads/{branch}").split("\t")[0] or ZERO
    if old != ZERO:
        git("fetch", "--quiet", remote, branch)
    current = version_at(head)
    kind = RELEASE if release else TEST
    refspecs = [f"HEAD:refs/heads/{branch}"]
    new_tag = None
    if not (tags.get(current) == head and kind.match(current)) and (release or needs_version(remote, head, old)):
        local_tags = git("tag", "--list").splitlines()
        new_tag = next_version(set(tags) | set(local_tags), release)
        with open(MANIFEST, encoding="utf-8", newline="") as f:
            text = f.read()
        text, found = re.subn(r'("version"\s*:\s*")[^"]*(")', rf"\g<1>{new_tag}\g<2>", text, count=1)
        if not found:
            sys.exit(f"No version in {MANIFEST}.")
        if new_tag != current:
            with open(MANIFEST, "w", encoding="utf-8", newline="") as f:
                f.write(text)
            git("commit", "--quiet", "-m", f"Version {new_tag}", "--", MANIFEST)
        git("tag", new_tag)
        refspecs.append(f"refs/tags/{new_tag}")
    # Atomic: if someone else took the number meanwhile, the branch doesn't move either.
    if subprocess.run(["git", "push", "--atomic", remote, *refspecs]).returncode:
        if new_tag:
            git("tag", "--delete", new_tag)
        sys.exit("Push failed. Nothing changed on the remote; fix the cause and run this again.")
    print(f"Pushed {branch} as {new_tag or current}." if new_tag or kind.match(current)
          else f"Pushed {branch}; no mod change, so no new version.")


def main(argv=None) -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("push")
    p.add_argument("branch")
    p.add_argument("--release", action="store_true")
    p.add_argument("--remote", default="origin")
    h = sub.add_parser("hook")
    h.add_argument("remote")
    args = parser.parse_args(argv)
    if args.command == "push":
        push(args.remote, args.branch, args.release)
        return
    problems = check(args.remote, sys.stdin.read().splitlines())
    if problems:
        sys.exit("\n".join(["Push refused: every pushed mod change needs its own version.", *problems,
                            "Use: python tools/version.py push <branch>"]))


if __name__ == "__main__":
    main()

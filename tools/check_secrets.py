"""
tools/check_secrets.py — Refuse to publish private values.

Copied from slay-my-stats, plus the mod's config file and Lambda URLs.

Run by githooks/pre-commit (what is about to be committed) and githooks/pre-push
(every tracked file, so the deploy can't publish a leak either), so a slip is
caught before it reaches GitHub rather than after.

    python tools/check_secrets.py --staged   # pre-commit: staged files
    python tools/check_secrets.py            # pre-push: all tracked files

Exit status is 1 and the offending lines are printed when something matches.

What it looks for:

    blocked paths     secrets files that must never be committed (the mod's
                      SlayMyStats.config.json with the real endpoints)
    Lambda URLs       a real *.lambda-url.<region>.on.aws address
    Steam IDs         17-digit ids starting 7656119. This project keeps them out
                      of the repo on purpose.
    AWS keys          every documented key-id prefix (A3T, AKIA, AGPA, AIDA,
                      AROA, AIPA, ANPA, ANVA, ASIA), a 40-char secret access key
                      where the line names one, the Bedrock key formats, and PEM
                      private key blocks
    AWS account ids   a 12-digit account number inside an ARN, or written out
                      where the line calls it an account id
    emails            anything that isn't an example/test domain
    public IPv4       addresses outside the private, loopback, link-local and
                      RFC 5737 documentation ranges

The pattern set follows awslabs/git-secrets, minus the parts that only make
sense with its per-repo config. What is deliberately NOT covered: a bare 40-char
secret with no context (every base64 blob in a codebase matches) and entropy
heuristics.

A deliberate value is allowed in three ways, all greppable:

    ALLOW_* sets   below, with a reason (the preferred one for a recurring value)
    .gitallowed    a repo-root file of allowed regexes, one per line, `#` comments
                   (git-secrets' mechanism: good for one-off false positives)
    secrets-ok     anywhere in the offending line
"""

import re
import subprocess
import sys
from pathlib import Path

# --- values that are deliberately fine -------------------------------------

# Fabricated ids used by tests and examples. Explicit rather than pattern-based
# so a real id can't be waved through by looking test-ish.
ALLOW_STEAM_IDS: set[str] = set()

# Non-routable / documentation addresses, plus literals that merely look like an
# address in this repo.
ALLOW_IPS: set[str] = set()

ALLOW_EMAIL_DOMAINS = ("example.com", "example.org", "example.net")
ALLOW_EMAIL_SUFFIXES = (".example", ".invalid", ".test", ".localhost")

# The key id and secret AWS prints in its own documentation, which git-secrets
# allowlists for the same reason: quoting the docs example must not block a
# commit.
ALLOW_AWS_KEY_IDS = {"AKIAIOSFODNN7EXAMPLE"}
ALLOW_SECRET_KEYS = {"wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"}

# A committed secrets file is always wrong, whatever is in it today. The mod's
# own config holds the real endpoints; workshop/stage.ps1 writes the shipped one.
BLOCKED_PATHS = (
    "SlayMyStats.config.json",
    "infra/cdk.context.json",
    ".env",
    "id_rsa",
    "id_ed25519",
)

PATTERNS = [
    ("Steam ID", re.compile(r"\b7656119\d{10}\b")),
    # A real ingest address belongs in the config, not the repo. The examples use
    # placeholders (REPLACE-ME, xxxxxxxx), which don't match.
    ("Lambda Function URL", re.compile(r"\b[a-z0-9]{20,}\.lambda-url\.[a-z0-9-]+\.on\.aws\b")),
    # Every documented key-id prefix, not just AKIA/ASIA (the set awslabs/git-secrets
    # registers): A3T is the older format, the rest are the other IAM/GPA/ROA/... ids.
    ("AWS access key id",
     re.compile(r"\b(?:A3T[A-Z0-9]|AKIA|AGPA|AIDA|AROA|AIPA|ANPA|ANVA|ASIA)[A-Z0-9]{16}\b")),
    # A bare 40-char base64 run matches half a codebase, so this only fires when
    # the line says what it is.
    ("AWS secret access key",
     re.compile(r"(?i)\baws[_\-.]?secret[_\-.]?access[_\-.]?key\b\s*[:=]\s*[\"']?([A-Za-z0-9/+=]{40})[\"']?")),
    ("Bedrock API key", re.compile(r"\bABSK[A-Za-z0-9+/]{109,}=*")),
    ("Bedrock API key", re.compile(r"\bbedrock-api-key-[A-Za-z0-9+/=]{16,}")),
    ("private key block", re.compile(r"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    ("AWS account id in an ARN", re.compile(r"\barn:aws[a-z-]*:[a-z0-9-]*:[a-z0-9-]*:\d{12}\b")),
    # Written out by hand, either 123456789012 or 1234-5678-9012, but only where
    # the line calls it an account id -- bare 12-digit and dashed numbers are
    # timestamps, ports, build numbers and so on.
    ("AWS account id",
     re.compile(r"(?i)\baws[_\-.]?account[_\-.]?id\b\s*[:=]\s*[\"']?(\d{4}-?\d{4}-?\d{4})\b")),
    ("email address", re.compile(r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")),
    # Delimited by something other than a digit or dot, so decimal runs like
    # SVG path data (".82 1.13.16.45.68") don't read as addresses.
    ("public IPv4 address", re.compile(r"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])")),
]

SKIP_MARKER = "secrets-ok"

# Never echo these back: they are live credentials, not identifiers. Only the
# file and line are reported, which is enough to find them.
MASKED_KINDS = {"AWS secret access key", "Bedrock API key"}

GITALLOWED = ".gitallowed"  # repo-root file of allowed regexes, one per line


def is_private_ip(value: str) -> bool:
    try:
        a, b, c, _d = (int(part) for part in value.split("."))
    except ValueError:
        return False
    if a in (0, 10, 127) or a >= 224:
        return True
    if (a, b) == (169, 254):
        return True
    if (a, b, c) == (192, 0, 2):
        return True
    if a == 172 and 16 <= b <= 31:
        return True
    if (a, b, c) in {(192, 168, 0), (198, 51, 100), (203, 0, 113)}:
        return True
    return False


def allowed(kind: str, value: str) -> bool:
    if kind == "Steam ID":
        return value in ALLOW_STEAM_IDS
    if kind == "AWS access key id":
        return value in ALLOW_AWS_KEY_IDS
    if kind == "AWS secret access key":
        return value in ALLOW_SECRET_KEYS
    if kind == "public IPv4 address":
        return value in ALLOW_IPS or is_private_ip(value)
    if kind == "email address":
        domain = value.rsplit("@", 1)[1].lower()
        return domain in ALLOW_EMAIL_DOMAINS or domain.endswith(ALLOW_EMAIL_SUFFIXES)
    return False


def load_gitallowed() -> list[re.Pattern]:
    """Regexes from <repo root>/.gitallowed, one per line, `#` starts a comment.

    The same idea as awslabs/git-secrets: a line matching any of them is not
    reported, which beats editing ALLOW_* for every one-off false positive.
    """
    root = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True)
    if root.returncode != 0:
        return []
    path = Path(root.stdout.decode("utf-8").strip()) / GITALLOWED
    if not path.is_file():
        return []
    allowed_regexes = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        try:
            allowed_regexes.append(re.compile(line))
        except re.error as exc:
            print(f"check_secrets: {GITALLOWED} line ignored (bad regex): {line!r}: {exc}",
                  file=sys.stderr)
    return allowed_regexes


def matched_value(match: re.Match) -> str:
    """The interesting part of a match: its last group when it captures one."""
    return match.group(match.lastindex) if match.lastindex else match.group(0)


def describe(kind: str, value: str) -> str:
    """Never print a live credential back out; the file and line locate it."""
    return f"{value[:4]}... ({len(value)} chars)" if kind in MASKED_KINDS else value


def scan_text(path: str, text: str, allowed_regexes: list[re.Pattern] = ()) -> list[str]:
    problems = []
    for lineno, line in enumerate(text.splitlines(), start=1):
        if SKIP_MARKER in line:
            continue
        if any(r.search(line) for r in allowed_regexes):
            continue
        for kind, pattern in PATTERNS:
            for match in pattern.finditer(line):
                value = matched_value(match)
                if allowed(kind, value):
                    continue
                problems.append(f"{path}:{lineno}: {kind}: {describe(kind, value)}")
    return problems


def tracked_files() -> list[str]:
    out = subprocess.run(["git", "ls-files", "-z"], capture_output=True, check=True).stdout
    return [p for p in out.decode("utf-8").split("\0") if p]


def staged_files() -> list[str]:
    out = subprocess.run(["git", "diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z"],
                         capture_output=True, check=True).stdout
    return [p for p in out.decode("utf-8").split("\0") if p]


def read(path: str, staged: bool) -> str | None:
    """The staged blob in --staged mode, else the file on disk. None if binary."""
    if staged:
        got = subprocess.run(["git", "show", f":{path}"], capture_output=True)
        if got.returncode != 0:
            return None
        data = got.stdout
    else:
        data = Path(path).read_bytes()
    if b"\0" in data[:8000]:
        return None
    return data.decode("utf-8", "replace")


def main() -> int:
    staged = "--staged" in sys.argv
    files = staged_files() if staged else tracked_files()
    allowed_regexes = load_gitallowed()

    problems = [f"{f}: blocked path (never commit this)"
                for f in files for p in BLOCKED_PATHS if f == p or f.endswith("/" + p)]
    for f in files:
        text = read(f, staged)
        if text is not None:
            problems += scan_text(f, text, allowed_regexes)

    if problems:
        print("Refusing to publish private values:", file=sys.stderr)
        for problem in problems:
            print(f"  {problem}", file=sys.stderr)
        print("\nIf it is deliberate, allow it: add the value to an ALLOW_* set in "
              f"tools/check_secrets.py, put a\nregex in {GITALLOWED} at the repo root, "
              f"or put `{SKIP_MARKER}` on the line.", file=sys.stderr)
        return 1

    scope = "staged files" if staged else "tracked files"
    print(f"check_secrets: {len(files)} {scope} clean.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

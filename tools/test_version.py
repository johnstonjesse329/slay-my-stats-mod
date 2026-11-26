"""python -m unittest discover tools -p test_version.py"""
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import version
from version import git

ROOT = Path(__file__).parent.parent


class Names(unittest.TestCase):
    def test_next(self):
        tags = ["v0.0.6", "v0.0.7-test.2", "v0.0.7-test.10", "v0.0.6-test.30"]
        self.assertEqual(version.next_version(tags, False), "v0.0.7-test.11")
        self.assertEqual(version.next_version(tags, True), "v0.0.7")
        self.assertEqual(version.next_version(["v0.0.9", "v0.0.10"], False), "v0.0.11-test.1")

    def test_shipped(self):
        self.assertTrue(version.shipped("SlayMyStatsCode/Ruler.cs"))
        self.assertTrue(version.shipped("manual/Install.ps1"))
        for path in ("README.md", "tools/version.py", "githooks/pre-push", "Probe/Stats.cs", ".gitignore"):
            self.assertFalse(version.shipped(path), path)


class Pushing(unittest.TestCase):
    """A scratch clone and bare remote, with the real hook (minus the secrets check)."""

    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(os.chdir, os.getcwd())
        git("init", "--quiet", "--bare", str(self.dir / "remote.git"))
        git("clone", "--quiet", str(self.dir / "remote.git"), str(self.dir / "clone"))
        os.chdir(self.dir / "clone")
        for key, value in (("user.name", "t"), ("user.email", "t@example.com"), ("core.hooksPath", "githooks")):
            git("config", key, value)
        for path in ("githooks/pre-push", "tools/version.py"):
            Path(path).parent.mkdir(exist_ok=True)
            shutil.copy(ROOT / path, path)
        Path("tools/check_secrets.py").write_text("")
        os.chmod("githooks/pre-push", 0o755)
        self.commit(version.MANIFEST, '{\n  "version": "v0.0.6"\n}\n')
        git("tag", "v0.0.6")
        git("push", "--quiet", "--no-verify", "origin", "HEAD:refs/heads/main", "v0.0.6")

    def commit(self, path, text):
        Path(path).write_text(text, newline="\n")
        git("add", "-A")
        git("commit", "--quiet", "-m", path)

    def run_push(self, *args):
        return subprocess.run([sys.executable, "tools/version.py", "push", *args],
                              capture_output=True, text=True)

    def plain_push(self, branch):
        return subprocess.run(["git", "push", "origin", f"HEAD:refs/heads/{branch}"],
                              capture_output=True, text=True)

    def test_flow(self):
        self.commit("a.cs", "1")
        for branch in ("main", "new"):
            refused = self.plain_push(branch)
            self.assertNotEqual(refused.returncode, 0)
            self.assertIn("v0.0.6 is already used", refused.stderr)

        done = self.run_push("test")
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertEqual(version.version_at("HEAD"), "v0.0.7-test.1")
        head = git("rev-parse", "HEAD")
        self.assertEqual(version.remote_tags("origin")["v0.0.7-test.1"], head)

        # The same build can go to a second branch without a new number.
        self.assertEqual(self.run_push("test2").returncode, 0)
        self.assertEqual(git("rev-parse", "HEAD"), head)

        # Docs need no version.
        self.commit("README.md", "docs")
        docs = self.plain_push("test")
        self.assertEqual(docs.returncode, 0, docs.stderr)

        # Another branch takes the next number.
        git("checkout", "--quiet", "-b", "other", "v0.0.6")
        self.commit("b.cs", "2")
        self.assertEqual(self.run_push("other").returncode, 0)
        self.assertEqual(version.version_at("HEAD"), "v0.0.7-test.2")

        self.assertEqual(self.run_push("main2", "--release").returncode, 0)
        self.assertEqual(version.version_at("HEAD"), "v0.0.7")

    def test_dirty_tree_refused(self):
        Path(version.MANIFEST).write_text("{}")
        self.assertIn("Commit your changes first", self.run_push("test").stderr)


if __name__ == "__main__":
    unittest.main()

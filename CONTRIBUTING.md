# Contributing

## Issues

Bug reports, questions and requests for API surface are welcome in the issue tracker of any SDK
repository. If you are not sure which repository an issue belongs to, open it in
[oni-sdk-docs](https://github.com/Salacious-Oni-Dev/oni-sdk-docs/issues).
Security problems are the exception: see [Security](#security) below.

A useful bug report gives the SDK release you installed, the game build, which `SimDLL.dll`
was installed, the other mods you had enabled, and the game's log (`Player.log`). The issue
template asks for each of these.

## Security

Please do not report a security problem in a public issue. Use GitHub's private vulnerability
reporting instead: open this repository's **Security** tab and choose **Report a
vulnerability**. The report is visible only to you and the maintainers, and the fix is
discussed there before anything is made public. If you are unsure which repository a problem
belongs to, report it in any of them.

A useful report says which release and game build you used, what an attacker needs (a file
you open, access to your machine, access to your network), what they can do with it, and how
to reproduce it. During the alpha only the latest release is supported, so check that the
problem is still there in it.

Some behaviour is documented and intended, such as a development tool that listens on the
network without authentication. That is not a vulnerability on its own. It is one if it
happens when the documentation says it does not, or reaches further than the documentation
says.

**In scope here:** the framework's swap of the simulation library at launch: the checks of the
game build and of the library's SHA-256 before it copies anything, the backup it keeps of the
game's own `SimDLL.dll`, and the restore at quit. Also how it reads `sim-tunables.json` and
its own marker files. `DebugInspectorServer` is known to listen on every network interface
with no authentication, and nothing starts it unless a mod calls it; a way to reach it when no
mod has started it, or to make it do more than its documented routes, is in scope.

## Pull requests are not accepted yet

During the alpha, the public repositories are generated from the development repositories at
each release. A commit made here would be replaced by the next release, so pull requests
cannot be merged.

If you have a fix, describe it in an issue, with a patch or a snippet if it helps. Fixes are
ported by hand, and the changelog entry for the fix links the issue.

## Licensing

A patch or snippet posted in an issue is taken as offered under this repository's license,
the MIT License.

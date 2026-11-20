# Release procedure

Target: https://github.com/dmitryalexander/word-craft
Version/tag: 2.3.2 / v2.3.2

1. Review this source snapshot and confirm the MIT LICENSE and THIRD-PARTY-NOTICES.md are included in source and portable packages.
2. Build from a clean checkout, run Test.ps1, and create the ZIP with Package.ps1. Verify `dist/SHA256SUMS.txt`.
3. Extract the portable ZIP into a separate writable folder and review typing, paste/read, Ctrl+A cloth selection/editing, Caps Lock options, audio levels, minimize/resume and close. Repeat on a second Windows machine. Confirm text is copied before closing.
4. Commit/push the prepared Repository directory contents to the target repository after review. Do not include personal workspace diaries, caches, live binaries or test captures.
5. Create/push tag `v2.3.2`. The workflow builds a draft release; review its attached ZIP/checksums and notes before publishing. Manual workflow runs only create downloadable build artifacts.

Prepared files are local only. No commit, push, tag or public release is performed by preparation. Local graphics regression checks do not establish creative acceptance or broad hardware compatibility.

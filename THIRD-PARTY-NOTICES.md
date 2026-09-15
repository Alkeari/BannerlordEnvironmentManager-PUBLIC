# Third-party notices

Work by other people that Bannerlord Environment Manager builds on. Each entry says what was used,
who wrote it, and under what license. This file covers what ships in the BEM executable;
development-only tooling (test frameworks, build tools) is not distributed and is not listed.

---

## Libraries compiled into BEM

| Library | Author | License |
|---|---|---|
| Microsoft Windows App SDK | Microsoft | Microsoft Software License Terms |
| CommunityToolkit.Mvvm | Microsoft and the .NET Foundation | MIT |
| System.Management | Microsoft | MIT |
| System.Diagnostics.EventLog | Microsoft | MIT |
| ICSharpCode.Decompiler (ILSpy) | ILSpy Contributors | MIT |
| Microsoft.Diagnostics.Runtime (ClrMD) | Microsoft | MIT |

**ICSharpCode.Decompiler** deserves the named credit: it is the decompiler engine behind ILSpy
(https://github.com/icsharpcode/ILSpy), a community open-source project, and it is what lets BEM read
the code behind a crash frame without leaving the app. MIT license, Copyright (c) ILSpy Contributors.

The MIT license text reproduced at the bottom of this file applies to each MIT-licensed library
above, under its own copyright holder. The Windows App SDK is distributed under Microsoft's own
software license terms (https://aka.ms/windowsappsdk/license), not MIT.

---

## Services and ecosystems BEM talks to

- **Nexus Mods API** (https://www.nexusmods.com) - update checking, mod page identification and
  Mod Manager downloads, always with the key Nexus issues when the user signs in and within Nexus's acceptable
  use policy.
- **BUTR** (https://butr.link) - community crash-report scores are read from, and crash evidence can
  be contributed back to, the BUTR services that also maintain BLSE, ButterLib and the wider
  Bannerlord modding toolchain.
- **Harmony** (https://github.com/pardeike/Harmony) - BEM's companion module compiles against the
  0Harmony assembly shipped by the user's own Bannerlord.Harmony install; BEM does not distribute
  Harmony itself.

---

## Calradia Warden

**Author:** Rely1234 (`mazetankzz-gif` on GitHub)
**Project:** https://github.com/mazetankzz-gif/calradia-warden
**License:** MIT

BEM's **Mod Safety** page is a reimplementation of Calradia Warden. The original is a short, read-only
PowerShell script its author wrote after being hit by a trojanized Steam Workshop mod that pulled a
hidden PowerShell payload from the internet and ran it, hidden, on every game launch.

What BEM took from it: the idea, the verdict vocabulary, the read-only rule, the categories of
behavior worth watching for, and the shape of the community blocklist, including the rule that a mod
is matched only by Steam Workshop id or by the exact SHA-256 of a file it ships, never by name, so no
innocent author is accused by a coincidence of naming.

What BEM did differently: the implementation is C#, reading assembly metadata rather than sweeping
whole files as text; it also reads downloaded archives, so a payload can be caught before it is
extracted; and several rules were narrowed after measuring them against a real 200-mod install. None
of the original script is vendored or executed.

BEM reads the blocklist from the Calradia Warden repository's own published `blocklist.json`, which is
where reports are collected. That read is optional and can be switched off.

### MIT License

Copyright (c) 2026 Rely1234

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

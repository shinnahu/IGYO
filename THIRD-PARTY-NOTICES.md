# Third-party notices

IGYO's own code is in this repository. What follows is made by others and comes with IGYO, each part under
its own terms.

## Microsoft Edge WebView2 Runtime (the engine)

The setup program published under Releases contains Microsoft's WebView2 Runtime, Fixed Version (x64), taken
from Microsoft's own package; the version is in the release notes and in Settings > About. It is not in this
repository, and it is not under IGYO's licence. Copyright (c) Microsoft Corporation. It is licensed, not
sold, under the Microsoft Software License Terms for the Microsoft Edge WebView2 Runtime, which Microsoft
publishes at https://developer.microsoft.com/microsoft-edge/api/eula/webview2 (the setup window's "Read the
terms" link shows them as a page). Installing IGYO means accepting those terms for the engine. Microsoft
documents this way of shipping the engine with an application at
https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution#the-fixed-version-runtime-distribution-mode

The runtime is built on Chromium and other open source software. Its own list of third-party licences is shown
by `show_third_party_software_licenses.bat` in the runtime folder of an installed IGYO.

**Widevine is not in the published setup.** Microsoft's package also contains Google's Widevine Content
Decryption Module (the folder `WidevineCdm`), which is not open source and whose own licence file says it may
not be distributed without an agreement with Google. The published setup is built without that folder.
The engine downloads the module by itself a few minutes after it first runs.
`build.ps1 -KeepWidevine` keeps the folder, for a build you make for yourself from Microsoft's package.

## Microsoft.Web.WebView2 (the SDK that connects IGYO to the engine)

`Microsoft.Web.WebView2.Core.dll`, `Microsoft.Web.WebView2.WinForms.dll`, `Microsoft.Web.WebView2.Wpf.dll`
and `WebView2Loader.dll`, from the NuGet package Microsoft.Web.WebView2 1.0.4314.35.

    Copyright (C) Microsoft Corporation. All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions are
    met:

       * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.
       * Redistributions in binary form must reproduce the above
    copyright notice, this list of conditions and the following disclaimer
    in the documentation and/or other materials provided with the
    distribution.
       * The name of Microsoft Corporation, or the names of its contributors
    may not be used to endorse or promote products derived from this
    software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
    "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
    LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
    A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
    OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
    SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
    LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
    DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
    THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
    (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
    OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The package's own notice names two components it incorporates, both under the BSD 3-Clause licence:
Antlr3.Runtime 3.5.2-rc1 and StringTemplate4 4.0.9-rc1.

    Copyright (c) 2011 The ANTLR Project
    All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions
    are met:

     1. Redistributions of source code must retain the above copyright
        notice, this list of conditions and the following disclaimer.
     2. Redistributions in binary form must reproduce the above copyright
        notice, this list of conditions and the following disclaimer in the
        documentation and/or other materials provided with the distribution.
     3. Neither the name of the copyright holder nor the names of its
        contributors may be used to endorse or promote products derived from
        this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
    IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
    OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
    IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT, INDIRECT,
    INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
    NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
    DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
    THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
    (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
    THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## EasyList (the ad block list)

`Assets\easylist.txt` is EasyList, unchanged, by The EasyList authors (https://easylist.to/). EasyList is dual
licensed under the GNU General Public License version 3 (or later) and the Creative Commons
Attribution-ShareAlike 3.0 Unported licence (or later); it is included here under the Creative Commons
Attribution-ShareAlike 3.0 Unported licence, https://creativecommons.org/licenses/by-sa/3.0/. The list's own
licence page is https://easylist.to/pages/licence.html. "Update now" in Settings > Ad block downloads the
current list from https://easylist.to/easylist/easylist.txt.

## Fonts

Ten font families come with IGYO for its themes, in `Assets\Shared\fonts`: Averia Libre, Barlow Condensed, Barlow Semi
Condensed, Cormorant Garamond, Fredoka, Google Sans Flex, JetBrains Mono, M PLUS 1, Mochiy Pop One and Saira. Each is under the SIL Open
Font License, Version 1.1. Each family's licence text with its copyright notice is beside the font files
(`LICENSE-<family>.txt`), and `README.txt` in that folder lists every file with where it came from and its
SHA-256.

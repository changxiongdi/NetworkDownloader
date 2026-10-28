# Third-party components

The BT worker links libtorrent-rasterbar 2.1.2, Boost 1.92.0 and OpenSSL 3.6.5.
Versions and source archive checksums are pinned by native/vcpkg.json's baseline.

- libtorrent: https://github.com/arvidn/libtorrent — BSD-3-Clause and bundled component notices.
- Boost: https://www.boost.org — Boost Software License 1.0.
- OpenSSL: https://www.openssl.org — Apache License 2.0 and bundled component notices.

The build script copies the installed packages' full copyright/license files into
third-party-licenses/ in the distribution. Keep those files with binary redistributions.
vcpkg and its build tools are build-time dependencies, not shipped with the application.

## Managed task catalog

- Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core 10.0.14 and
  System.Security.Cryptography.ProtectedData 10.0.14: Microsoft, MIT license.
- SQLitePCLRaw bundle/config/core/provider 3.0.5: Copyright 2014-2026 SourceGear,
  LLC, Apache License 2.0. The full Apache 2.0 text is included in `openssl.txt`
  in the same distribution license directory. Source: https://github.com/ericsink/SQLitePCL.raw
- SQLite native package 3.53.4: https://sqlite.org/ ; its full package notice is
  copied to `sqlite-native.txt`. The previous vulnerable SQLite native bundle
  is not used. The absence of current NuGet advisories is not a security guarantee.

MIT License (Microsoft components)

Copyright (c) .NET Foundation and Contributors.
Copyright (c) Microsoft Corporation. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

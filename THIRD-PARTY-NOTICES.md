# Third-Party Notices

TimecodeSyncPlayer uses the following third-party components at runtime. Test-only dependencies
are not part of the distributed application and are intentionally omitted.

## libmpv / mpv

libmpv is **not included** in the TimecodeSyncPlayer release zip. The project only provides an
installation script and instructions for obtaining a separate upstream Windows build.

- Project: [mpv](https://github.com/mpv-player/mpv)
- License: GPL version 2 or later by default, or LGPL version 2.1 or later when built with
  `-Dgpl=false`. The selected Windows build and its dependencies determine the effective terms.
- License texts: [GPL-2.0](https://github.com/mpv-player/mpv/blob/master/LICENSE.GPL) and
  [LGPL-2.1](https://github.com/mpv-player/mpv/blob/master/LICENSE.LGPL)
- Copyright and per-file details: [mpv Copyright](https://github.com/mpv-player/mpv/blob/master/Copyright)

Users who install libmpv are responsible for retaining the notices and complying with the terms
provided by that binary's distributor, including the terms of bundled FFmpeg and other libraries.

## SpoutDX / Spout2

`SpoutDX.dll` is included in the TimecodeSyncPlayer release zip.

- Project: [Spout2](https://github.com/leadedge/Spout2)
- License: Simplified BSD License (SPDX: BSD-2-Clause)

The current upstream license is reproduced below. This is the precise two-clause license published
by Spout2; it supersedes the older "BSD-3" shorthand in the release preparation plan.

> Copyright (c) 2020-2024, Lynn Jarvis
> All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification, are permitted
> provided that the following conditions are met:
>
> 1. Redistributions of source code must retain the above copyright notice, this list of conditions
>    and the following disclaimer.
>
> 2. Redistributions in binary form must reproduce the above copyright notice, this list of
>    conditions and the following disclaimer in the documentation and/or other materials provided
>    with the distribution.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
> IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
> FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
> CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
> DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
> DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
> IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
> OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## NAudio 2.2.1

- Project: [NAudio](https://github.com/naudio/NAudio)
- License: MIT

> Copyright 2020 Mark Heath
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute,
> sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial
> portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
> NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
> NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
> DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
> OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Serilog 4.0.0 and Serilog.Sinks.File 6.0.0

- Projects: [Serilog](https://github.com/serilog/serilog) and
  [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file)
- Copyright: Serilog Contributors
- License: [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0)

Both distributed NuGet packages declare the SPDX expression `Apache-2.0`. The linked Apache Software
Foundation page is the complete, authoritative license text.

## Microsoft.Extensions.DependencyInjection 10.0.7

- Project: [.NET](https://github.com/dotnet/dotnet)
- License: MIT

> Copyright (c) .NET Foundation and Contributors
> All rights reserved.
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute,
> sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial
> portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
> NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
> NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
> DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
> OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The Microsoft package also carries its own
[third-party notices](https://github.com/dotnet/dotnet/blob/main/THIRD-PARTY-NOTICES.TXT), which
cover components incorporated into the package.

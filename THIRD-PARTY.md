# Third-party software

ClipIt uses unmodified components from the official OBS Studio 33.0.1 Windows x64 distribution:

- libobs and the Direct3D 11 / Windows capture graphics components.
- win-capture, win-wasapi, obs-ffmpeg, obs-nvenc and obs-qsv11 modules.
- OBS encoder-probing and FFmpeg muxer helper executables.
- Transitive DLL dependencies supplied by the official OBS package, including FFmpeg and related codec/transport libraries.

OBS Studio is copyright its contributors and licensed under GNU GPL version 2 or later. See `LICENSE.txt`; matching source is supplied in `licenses/OBS-Studio-33.0.1-Sources.tar.gz`, including component notices and build instructions. Original OBS binaries have not been modified. Their packaging has been reduced to remove the OBS Studio frontend and unrelated modules.

Upstream: https://github.com/obsproject/obs-studio/tree/33.0.1

Official binary archive: https://github.com/obsproject/obs-studio/releases/download/33.0.1/OBS-Studio-33.0.1-Windows-x64.zip

Binary archive SHA-256: `0D9436451C2D772B33428E3FDAE9776F071895C335DE17283176306FB72651D3`.

Upstream Windows dependencies and build instructions are referenced by the matching OBS source tree and its `buildspec.json`. If redistributing this preview, review the corresponding-source requirements of all bundled dependencies, obtain their matching source/build material from upstream, and retain the applicable notices.

Microsoft .NET 10 runtime files are included for standalone execution. Their license and notices are in `app/licenses/`, copied from the Microsoft runtime packages. ClipIt does not modify those runtime files.

ClipIt's own source is provided under GPL-2.0-or-later. No affiliation with or endorsement by the OBS Project is implied.

## Clip editor media tools

`app/tools/ffmpeg.exe` and `ffprobe.exe` are the unmodified Gyan FFmpeg 9.0.1 essentials static Windows build. They run as separate, on-demand processes for metadata, thumbnails, separate audio preview and rendering. The supplied GPL v3 license, build configuration, upstream source pointer and FFmpeg source archive are in `licenses/ffmpeg/`. These tools have their own license; ClipIt's own code remains GPL-2.0-or-later.

- Build provider: https://www.gyan.dev/ffmpeg/builds/
- Matching FFmpeg source: https://github.com/FFmpeg/FFmpeg/commit/bf1b838f2a
- ffmpeg.exe SHA-256: `72A489ECCD008C2EC2C0A5856C5C75BC3D8BBFA90166C4566865C246445E6AA3`
- ffprobe.exe SHA-256: `19202B23C0043F15AD1B7BCE2344F406FD52BD6EFD8F995CE02E7392A1CEC52F`

Retain the provider's notices and obtain the corresponding source/build material for statically linked dependencies if redistributing these tools.


## Windows installer and updater

Velopack 1.2.161 is licensed under MIT; the notice is included in licenses/Velopack-MIT.txt. It supplies the update library, installer and update helper. The matching version is pinned in the project and .config/dotnet-tools.json. Upstream: https://github.com/velopack/velopack/tree/92d6a1c91716729d449034df5c50307dcce39493

# Native second monitor · Morror 0.11.1

An Android phone or tablet can act as an extended Windows display. Windows applications remain ordinary desktop applications; Android runs the native **Morror Monitor** receiver, without a browser or WebView. The Windows UI currently uses Russian labels.

## Quick start

1. Open **«Второй экран»** (Second screen) in Morror.
2. Install the included virtual display component once and approve the Windows administrator prompt. Morror verifies the driver files and the Windows-trusted catalog signature before using SetupAPI/NewDev to install the unmodified driver package. Test signing and third-party root certificates are not enabled.
3. Connect and authorize Android by USB or Wi-Fi, select the device and enable the second monitor.
4. Morror installs its APK and starts the receiver automatically. Some Android vendors require a separate setting that allows installation through USB debugging.
5. Move a Windows application onto the virtual monitor, which is attached to the right of the existing desktop when Morror activates it.

Requirements: Windows 10/11 x64, .NET Framework 4.8, Windows Media Foundation, Android 8.0+ and H.264 decoding through Android MediaCodec. The display component and the first registration of a previously unsupported display size require administrator approval. Root access on Android is not required.

The receiver's Disconnect button hides after three seconds to avoid covering Windows controls. On a letterboxed view, touching an empty margin reveals it again. Android's normal Back gesture also closes the receiver.

## Device-native full-screen resolution

**Auto · device-native resolution** is the default. Morror reads the effective Android display size, including any system resolution override, and uses its landscape dimensions for both the Windows virtual monitor and the video stream. A Pixel 8 therefore uses **2400 × 1080**, instead of a fixed 1920 × 1080 desktop with a different aspect ratio.

HD and Full HD are long-edge budgets, not forced 16:9 modes:

| Pixel 8 setting | Monitor and stream |
| --- | --- |
| Auto | 2400 × 1080 |
| Full HD | 1920 × 864 |
| HD | 1280 × 576 |

The image keeps the device's aspect ratio and touch coordinates cover the same Windows monitor. Rare odd dimensions are rounded to even values required by H.264 YUV420.

If the driver does not advertise the requested size and refresh rate, Morror requests Windows administrator approval, adds that mode to `vdd_settings.xml`, retains a backup and refreshes only the MttVDD device. Existing resolutions, GPU settings, comments, monitor counts and other options are preserved. Physical graphics adapters are not restarted. Subsequent connections using an already supported mode do not need that registration step. Cancelling the prompt stops setup with an explanation instead of silently substituting a different aspect ratio.

## USB

Enable USB debugging, connect a data-capable cable, unlock Android and authorize the PC. Select USB in the second-monitor dialog and choose the wired device.

Video, audio and touch share a session-owned `adb reverse --no-rebind tcp:<port> tcp:<port>` tunnel. Wi-Fi is not required for local transmission. Morror does not replace another tool's forwarding rule or restart the shared ADB server.

## Wi-Fi and QR pairing

Select Wi-Fi and **«Подключить телефон по QR-коду»**. On Android 11+, open **Developer options → Wireless debugging → Pair device with QR code**, then use that system scanner. This is the same pairing flow used for Android mirroring. A previously authorized wireless device can simply be selected from the list.

After pairing, Morror installs and starts the receiver through ADB. The native stream travels over an ADB reverse tunnel through the authorized connection. Modern Android QR/TLS pairing provides the wireless-debugging transport encryption; no cloud relay or public HTTP server is used. Legacy `adb tcpip` connections should be used only on trusted networks.

## Touch and the mouse cursor

Windows receives native `InjectTouchInput` contacts in the virtual monitor's coordinates. Windows can also promote touch into mouse messages. While its injected contacts are active, Morror suppresses only touch-generated mouse movement on its virtual monitor. Physical mouse events and other mouse sources continue through the normal input chain. It does not continually warp the cursor back with `SetCursorPos`.

Touch can activate another Windows window and change keyboard focus, as on a normal touchscreen. The receiver does not create an independent Windows user session. Protected or elevated windows, DRM video and specialized input applications can impose restrictions.

The current client uses landscape orientation. Automatic monitor rotation, pen pressure, tilt and palm rejection are not implemented.

## Video, audio and teardown

The Windows host captures the virtual display directly and encodes H.264 using a separate LGPL FFmpeg process. Encoder probing tries NVIDIA NVENC, hardware Media Foundation and software Media Foundation. Available quality levels are native, HD and Full HD, with 30/60 FPS targets. Configuration targets are not guarantees of sustained FPS or latency.

Android decodes through MediaCodec into a SurfaceView. Its screen stays awake during transmission; the keep-awake flag is cleared when the session ends. The desktop does not launch a browser or a screen-sharing picker.

Audio is the current default Windows output mix, captured through WASAPI loopback and sent as stereo PCM float to Android AudioTrack. It can include other applications. Morror does not change the default audio endpoint or Windows volume.

Stop closes the encoder, stream, touch filter and only the session's own ADB reverse port. A virtual monitor activated by that session is detached; a monitor that was already active remains active. The driver and Android application stay installed for later use. Restart the session after changing the display arrangement.

The APK uses a local build signing key under the ignored `tools/native-monitor` directory. Preserve that key for APK updates. Android may require manual removal of an older Morror Monitor APK if a replacement was signed with a different key.

## Validation status

- Native protocol tests cover authentication, packet bounds, ordered touch contacts and H.264 NAL/AUD boundaries across arbitrary input fragments.
- Synthetic video passes through the real encoder, TCP server and an independent H.264 decoder. Tests verify session cleanup and listening-port closure.
- A dedicated Windows input fixture verifies native touch and scoped cursor-movement filtering, accounting for concurrent physical mouse events and a two-pixel rounding tolerance.
- Resolution tests cover wide phones, 16:10 and 4:3 tablets, Android overrides, even dimensions, preserving existing driver settings, atomic replacement and configuration backups.
- A real **Pixel 8 over Wi-Fi** decoded 60 frames at **2400 × 1080**, returned touch events and displayed the synthetic pattern at both screen edges without black side bars. The screenshot was visually checked; personal desktop content was not captured for that test.
- The installed Windows virtual monitor advertised and accepted **2400 × 1080 at 60 Hz**, while physical monitor sizes, positions and primary selection were preserved.

A physical USB cable, extended-duration sessions, measured latency and system-audio behavior on physical hardware still need separate validation. This implementation does not claim complete SuperDisplay feature parity.

## Development

Run:

```powershell
./scripts/get-native-monitor-dependencies.ps1 -BuildTools
./scripts/build-android-monitor.ps1
./build.ps1 -Test
./scripts/test-second-screen.ps1
```

JDK, Android SDK, FFmpeg and VDD archives are pinned by version and SHA-256. Development toolchains are not included in the installed application.

`tests/NativeAndroidSmoke.cs` exercises the APK on an explicitly selected authorized device using synthetic video. `tests/NativeResolutionSmoke.cs` explicitly applies a mode already registered for the virtual adapter and verifies physical monitor geometry; it changes the selected virtual monitor's mode and is not part of the default automated suite.

Implementation:

- `android/`: native receiver, MediaCodec, AudioTrack and touch events.
- `src/SecondScreenDialog.cs`: UI, APK installation, pairing and session lifecycle.
- `src/MonitorResolution.cs`: device dimensions and preserving driver configuration.
- `src/VirtualMonitorDriver.cs`: signed component installation, mode registration and virtual-display configuration.
- `src/NativeMonitor.cs`: bounded native TCP protocol and H.264 streaming.
- `src/ScreenTouchInjection.cs`: native touch and scoped mouse-promotion filtering.
- `src/WasapiMonitorAudio.cs`: shared-output audio capture.

See [third-party notices](../THIRD-PARTY-NOTICES.md) for component licenses and source links.

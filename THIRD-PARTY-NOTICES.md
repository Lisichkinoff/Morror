# Компоненты сторонних разработчиков

Morror использует официальный бинарный пакет **scrcpy 5.0.1** от Genymobile и поставляемые с ним ADB, SDL, FFmpeg и libusb. Файлы движка не изменены. Лицензия scrcpy находится в `runtime/LICENSE.txt` (Apache License 2.0).

Morror является отдельной Windows-оболочкой для scrcpy. Захват экрана, звука и управление Android выполняет scrcpy; интерфейс и дополнительные функции Morror разработаны в этом репозитории. Morror не является официальным продуктом Genymobile или авторов scrcpy.

Уведомления об авторских правах scrcpy, сохранённые из официального `LICENSE.txt`:

    Copyright (C) 2018 Genymobile
    Copyright (C) 2018-2026 Romain Vimont

Неизменённая копия этого файла для просмотра на GitHub: [licenses/scrcpy-LICENSE.txt](licenses/scrcpy-LICENSE.txt). Оригинальный проект: [Genymobile/scrcpy](https://github.com/Genymobile/scrcpy); основной автор: [Romain Vimont (rom1v)](https://github.com/rom1v).

Исходники и инструкции сборки компонентов:

- scrcpy: https://github.com/Genymobile/scrcpy/tree/v5.0.1
- Инструкции сборки/зависимости: https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/build.md
- ADB (Android Open Source Project): https://android.googlesource.com/platform/packages/modules/adb/
- SDL: https://github.com/libsdl-org/SDL
- FFmpeg: https://ffmpeg.org/legal.html и https://ffmpeg.org/download.html
- libusb: https://github.com/libusb/libusb

Официальный архив: https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip

SHA-256: `b12a2c4ee8be317422451fc7dcf8ee20a71b5ea7ef9ad73ddd825a25316227a5`.

При дальнейшем распространении сохраняйте лицензии и уведомления компонентов и соблюдайте условия их распространения. Исходники оболочки Morror находятся в папке `src` этого проекта.

## Генерация QR-кода

В сборку входит **QRCoder 1.8.1**, лицензия MIT. Полный текст лицензии с уведомлениями об авторских правах находится в `LICENSE-QRCoder.txt` рядом с приложением.

- Исходники: https://github.com/Shane32/QRCoder/tree/d050cc7c0bc986f14cfedf24b947e0ec5c1a7736
- Пакет: https://www.nuget.org/packages/QRCoder/1.8.1
- SHA-256 архива NuGet: `d3fe5c80c742b6a2ef8a31734c06600e2d674806a97f36f4af97b0d2871de309`.

Только для разработки и тестов используется **ZXing.Net 0.16.11** (https://www.nuget.org/packages/ZXing.Net/0.16.11). Он проверяет считывание QR независимым декодером и не поставляется с Morror. SHA-256 тестового пакета: `7d39234d668e558b3d374d18116ed57021d7c0df482801f1e1db4f1db9314ec3`.

## Установщик

Установщик создан с помощью **Inno Setup 6.7.3** (Copyright © Jordan Russell; portions Copyright © Martijn Laan), https://jrsoftware.org/. Неизменённый компилятор используется из неофициальной переносимой упаковки **Tools.InnoSetup 6.7.3** в NuGet: https://www.nuget.org/packages/Tools.InnoSetup/6.7.3. Подпись исполняемого файла компилятора проверена Windows. SHA-256 пакета: `f780898e402ff80612cc8d9fcb8c6e02932bd1cb4c900ffdaa31f9341cfb49f4`. Компилятор не входит в устанавливаемое приложение; исходный код Inno Setup доступен по адресу https://github.com/jrsoftware/issrc/tree/is-6_7_3.

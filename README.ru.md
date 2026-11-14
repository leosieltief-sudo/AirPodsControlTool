# AirPods Control для Windows

[English](README.md) · **Русский**

Небольшая программа в трее Windows 10/11 (x64) для AirPods: заряд и режимы
**Шумоподавление / Off / Прозрачность / Адаптивный**, а также набор режимов, между которыми переключает удержание ножки.
.NET 10, WPF, WinRT. Без браузера, облака и регистрации.

![screenshot](docs/screenshot.png)

> **Статус: ранняя версия, проверена на одном ПК.** Всё, что ниже отмечено как проверенное, проверено на одной машине
> (Windows 11 26200, Bluetooth MediaTek MT7922, AirPods Pro 3). Другие адаптеры и модели AirPods не проверялись.

## Что работает

| Функция | Как | Проверено на железе |
|---|---|---|
| Поиск сопряжённых AirPods, модель, подключение | API Windows, без настройки | ✅ |
| Заряд L / R / кейс (шаг 10 %), зарядка, «в ухе» | BLE-реклама, без настройки | ✅ |
| Переключение ANC / Off / Прозрачность / Адаптивный | WSL2-мост (ниже) или драйвер MagicAAP | ✅ через WSL2-мост (на слух); путь MagicAAP не проверен |
| Точный заряд (1 %) | WSL2-мост, по запросу | ✅ |
| Режимы удержания ножки (в том числе Off), записываются в AirPods | WSL2-мост, Settings → *Press and hold* | ✅ применяются (на слух); сохранение после переподключения и кейса **не проверено** |
| Горячие клавиши, автозапуск, меню в трее | — | ✅ |

Через мост AirPods не присылают подтверждения, поэтому программа пишет о командах **«sent» (отправлено)**, а не «confirmed».

## Почему это сложно в Windows

AirPods управляются протоколом Apple AAP через Bluetooth-канал L2CAP (PSM 0x1001). В Windows такой канал могут открыть только
драйверы режима ядра: Winsock отклоняет L2CAP-сокет (`WSAESOCKTNOSUPPORT`), а в WinRT такого API нет.
Подробности и источники — [docs/PROTOCOL.md](docs/PROTOCOL.md).

Обходных путей два:

1. **Driverless mode (WSL2-мост)** — рекомендуется: без Test Mode, без отключения Secure Boot, без драйвера.
   На время каждой команды программа передаёт USB-адаптер Bluetooth маленькому Alpine Linux в WSL2 (через usbipd-win),
   Linux отправляет AAP-команду, и адаптер всегда возвращается Windows.
   **Минус: на каждую команду Windows остаётся без Bluetooth примерно на 12–15 с** — звук прерывается, AirPods переподключаются.
2. **Драйвер MagicAAP** от автора MagicPods — мгновенно, но не подписан: нужно выключить Secure Boot и включить тестовый режим
   Windows. Программа его не устанавливает; см. [docs/WINDOWS-DRIVER-RESEARCH.md](docs/WINDOWS-DRIVER-RESEARCH.md).

## Требования (driverless mode)

- Windows 10/11 x64, виртуализация включена в BIOS/UEFI.
- **Установлен WSL2.** Если нет — выполните `wsl --install` от администратора и перезагрузитесь; сама программа WSL не ставит.
- Bluetooth-адаптер, подключённый по **USB** (встроенный Bluetooth ноутбуков обычно такой).
- AirPods сопряжены с Windows (*Параметры → Bluetooth и устройства*).
- Интернет на время разовой настройки.

**Адаптеры:** проверены только MediaTek MT7921/MT7922/MT7925 (на практике MT7922). Драйверы Intel, Realtek и Broadcom в ядре
есть, но мастер не ставит для них прошивку — **скорее всего, пока не заработают**.

## Установка

1. Скачайте `AirPodsControl-x.y.z-win-x64-portable.zip` из [Releases](../../releases) и распакуйте в любую папку.
2. Запустите `AirPodsControl.exe`. SmartScreen может предупредить, потому что exe не подписан (*Подробнее → Выполнить в любом случае*).
3. Нажмите **Set up driverless mode (WSL2)** (или *Settings → Driverless setup*). Мастер сам:
   - создаёт отдельный дистрибутив WSL `airpods-bridge` из Alpine minirootfs (~4 МБ, проверка SHA-256) и ставит в него
     BlueZ + Python (~40 МБ);
   - ставит [usbipd-win](https://github.com/dorssel/usbipd-win) через `winget`, если его нет;
   - **один раз** просит права администратора: открыть доступ к адаптеру через usbipd и скопировать ключ сопряжения
     **только этих AirPods** в этот дистрибутив;
   - для адаптеров MediaTek предлагает переключить WSL на ядро AirPods Control (см. ниже);
   - проверяет адаптер и связь с AirPods.

   Ставить Linux вручную не нужно. После настройки права администратора больше не требуются.
4. **Remove driverless mode** в мастере удаляет дистрибутив (вместе с ключом), закрывает доступ к адаптеру и убирает строку ядра.

### Ядро WSL (только MediaTek) — важно

В ядре WSL от Microsoft нет поддержки Bluetooth MediaTek. Ядро AirPods Control — это ядро Microsoft той же версии с включённым
MediaTek Bluetooth и встроенной прошивкой MT7921/MT7922/MT7925 ([kernel/](kernel/)). Оно прописывается в
`%UserProfile%\.wslconfig` (с резервной копией) и **действует на все ваши дистрибутивы WSL** (Ubuntu, Docker Desktop и т. д.).

> **О релизах:** лицензия прошивки MediaTek разрешает её распространение, но *не в составе ядра Linux*. Пока вопрос не решён,
> публичные релизы могут выходить **без** ядра; его можно собрать самостоятельно скриптом `kernel/build-kernel.sh`
> (≈30 мин) — см. [docs/RELEASING.md](docs/RELEASING.md).

## Использование

- **Кнопки режимов и горячие клавиши** (`Ctrl+Alt+1` ANC, `Ctrl+Alt+2` Off, `Ctrl+Alt+3` Прозрачность; настраиваются).
  Нажатия во время идущего переключения объединяются с ним (побеждает последнее) и не вызывают ещё одного разрыва Bluetooth.
- **Заряд:** *Refresh battery* ждёт свежую BLE-рекламу (без прерывания, ±10 %). *Exact (AAP)…* после предупреждения читает
  точные значения через мост. Старые значения остаются на экране с временем получения — нулём не заменяются.
- **Удержание ножки (Settings):** отметьте режимы, между которыми должна переключать ножка (не меньше двух; при Off
  отправляется и «Allow Off»), и нажмите **Apply to AirPods**. Флажки — это *желаемый* набор; строка под ними показывает, что
  AirPods действительно сообщили или подтвердили. Обычно там «Current AirPods set: unknown»: через мост AirPods ничего не сообщают.
  Назначение удержания (Siri или управление шумом) программа не меняет.
- **Защита Bluetooth-мыши и клавиатуры:** если через тот же адаптер подключены мышь или клавиатура (или это не удаётся
  определить), любая передача адаптера блокируется до явного подтверждения (по умолчанию «Нет»).

### CLI `airpodsctl.exe`

```
airpodsctl diag                       Bluetooth, сопряжённые AirPods, канал управления
airpodsctl battery 20                 заряд из BLE в течение 20 с
airpodsctl bridge-status              готовность driverless mode
airpodsctl bridge-check               какой адаптер будет передан и какие устройства ввода он обслуживает (ничего не меняет)
airpodsctl bridge-mode anc|off|transparency|adaptive[,режим…] [--trace]   переключить через мост, напечатать трассу операции
airpodsctl bridge-battery             точный заряд по AAP
airpodsctl bridge-hold-cycle --modes off,anc,transparency[,adaptive]     записать режимы удержания ножки
airpodsctl bridge-enable-off (--modes anc,… | --keep-current)            то же, Off добавляется всегда
airpodsctl kernel-status | kernel-install | kernel-remove                ядро WSL для MediaTek
  --allow-input-loss                  явно разрешить потерю Bluetooth-мыши/клавиатуры (общий --yes этого не делает)
```

## Сборка из исходников

Нужны .NET 10 SDK; Python 3 для тестов моста (необязательно); Inno Setup 6 для установщика (необязательно).

```
.\build.ps1             # тесты + self-contained single-file сборка win-x64 + zip в .\dist
.\build.ps1 -NoKernel   # то же, без ядра WSL для MediaTek
```

Ядро в git не хранится. Его собирает `kernel/build-kernel.sh` (от root в любом Alpine-дистрибутиве WSL) в `assets\wsl-kernel\`,
после чего `build.ps1` добавляет его в архив. Тесты: `dotnet test tests\AirPodsControl.Tests` (154) и
`python -m unittest discover -s tests/bridge` (61).

## Приватность

- Никакой телеметрии и учётных записей. Сеть нужна только при настройке (образ Alpine, пакеты Alpine, usbipd-win через winget).
- Ключ сопряжения AirPods копируется только в локальный дистрибутив WSL `airpods-bridge` и удаляется вместе с ним.
- Логи (`%LOCALAPPDATA%\AirPodsControl\logs`, 7 дней) содержат Bluetooth-адрес AirPods; настройки — в `%APPDATA%\AirPodsControl`.

## Документация

- [docs/WSL-BRIDGE.md](docs/WSL-BRIDGE.md) — устройство моста · [docs/BRIDGE-OPERATIONS.md](docs/BRIDGE-OPERATIONS.md) — одна
  операция = одна передача адаптера, трассы, защита устройств ввода, режимы удержания · [docs/PROTOCOL.md](docs/PROTOCOL.md) —
  форматы AAP и BLE с источниками · [docs/RELEASING.md](docs/RELEASING.md) — чек-лист релиза.

## Лицензии и благодарности

- AirPods Control: [MIT](LICENSE).
- Ядро WSL (только если входит в сборку): GPL-2.0 — исходники: [WSL2-Linux-Kernel](https://github.com/microsoft/WSL2-Linux-Kernel)
  от Microsoft (тег в `BUILDINFO`) и [kernel/patches](kernel/patches); встроенная прошивка MediaTek:
  [kernel/LICENCE.mediatek](kernel/LICENCE.mediatek).
- usbipd-win (GPL-3.0) и пакеты Alpine/BlueZ мастер скачивает из официальных источников, в сборку они не входят.
- Знания о протоколе: [LibrePods](https://github.com/librepods-org/librepods) (AAP),
  [AirPodsDesktop](https://github.com/SpriteOvO/AirPodsDesktop) (формат BLE, ID моделей),
  [AirPodsWindows](https://github.com/YimingZhanshen/AirPodsWindows) (интерфейс MagicAAP), [MagicPods](https://magicpods.app/magicaap/).

«AirPods» — товарный знак Apple Inc. Проект не связан с Apple.

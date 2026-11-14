# AirPods Control — техническое описание для передачи (состояние на 14.11.2026)

## 1. Что это
Нативная утилита для Windows 10/11 x64 (C#/.NET 10, WPF, MVVM), работает в трее. Показывает заряд AirPods (L/R/кейс)
и переключает режимы **Noise Cancellation / Off / Transparency / Adaptive**. Горячие клавиши по умолчанию: Ctrl+Alt+1/2/3,
конфликты определяются. Есть автозапуск, настройки и CLI `airpodsctl.exe` для диагностики.
Принципы: никаких выдуманных значений. Нет данных — показывается «—». Режим подсвечивается только по данным от AirPods,
а если подтверждения нет, это прямо написано в интерфейсе.

## 2. Железо тестового ПК
- AirPods Pro 3 (Apple product ID 0x2027).
- Bluetooth-адаптер MediaTek MT7922, USB `13d3:3622`.
- Windows 11 26200, WSL 2.7.3, Secure Boot включён, Test Mode выключен. **Требование пользователя: их не трогать.**

## 3. Почему всё сложно
AirPods управляются протоколом AAP через **L2CAP PSM 0x1001** (классический Bluetooth). В Windows L2CAP доступен только драйверам ядра:
Winsock возвращает `WSAESOCKTNOSUPPORT (10044)`, в WinRT такого API нет. Варианты:
1. **MagicAAP** — неподписанный драйвер ядра. Нужны Test Mode и выключенный Secure Boot. На этом ПК он установлен, но заблокирован (код 39), и **не используется**.
2. **Driverless Mode (WSL2 + usbipd)** — рабочий путь. Подробнее в §5.

## 4. Протокол (факты из LibrePods / AirPodsDesktop, код не копировался)
- Handshake: `00 00 04 00 01 00 02 00 00 00 00 00 00 00 00 00` → ответ `01 00 04 00 …`.
- Features: `04 00 04 00 4D 00 D7 00 00 00 00 00 00 00`. Запрос уведомлений: `04 00 04 00 0F 00 FF FF FF FF`.
- **Важно:** следующие пакеты слать только после ответа на handshake. Иначе AirPods ничего не присылают.
- Управление: `04 00 04 00 09 00 [id] d1 00 00 00`. Режим, id 0x0D: 01 Off, 02 ANC, 03 Transparency, 04 Adaptive.
  «Allow Off», id 0x34: 01 / 02. Нужен перед Off на Pro 2/Pro 3.
- Заряд, opcode 0x04: `[n]([comp] 01 [lvl] [status] 01)×n`. comp: 02 R, 04 L, 08 кейс. status: 01 заряжается, 02 нет, 04 отключён.
- «В ухе»: opcode 0x06. Метаданные: 0x1D. 0x2E — список подключённых устройств, 0x0E — audio source.
- BLE Proximity Pairing (без драйвера): manufacturer data 0x004C, тип 0x07, model ID little-endian, заряд в полубайтах ×10 %.

## 5. Driverless Mode: как работает одно переключение (~15 с)
1. `usbipd state` → BUSID адаптера по VID:PID.
2. `wsl --exec sleep 120` держит VM запущенной.
3. **Радио Bluetooth в Windows выключается** через WinRT `Radio.SetStateAsync`, без админа. Иначе Windows не отдаёт адаптер:
   usbipd отвечает `Device busy (exported)`. `bind --force` не используется, потому что отбирает адаптер у Windows насовсем.
   Флаг `RadioTurnedOff` в bridge.json нужен для восстановления после сбоя.
4. `usbipd attach --wsl --busid X` → в Alpine-дистрибутиве `airpods-bridge`: btusb + btmtk → hci0 (~0,1 с).
5. `bridge.sh run`: dbus + `bluetoothd -n --noplugin=policy,input,hog,network,sap,autopair`. Затем `btmgmt power on`
   с повторами: MT7922 переинициализируется, когда bluetoothd забирает адаптер.
6. `aap_bridge.py`: L2CAP 0x1001 (BT_SECURITY_MEDIUM, ключ сопряжения скопирован из Windows) → handshake → ждём ответ → 0x4D → 0x0F →
   (для Off: 0x34=01) → 0x0D=режим → ждём эхо 1,5 с. Последняя строка вывода: `RESULT {json}`.
7. **finally**: `usbipd detach`, радио Windows включается снова, AirPods сами переподключаются (за ≤13 с).
Все процессы скрыты (CREATE_NO_WINDOW) и помещены в Job Object с kill-on-close. При старте приложение делает recovery: detach и включение радио.

**Подтверждения нет.** Без аудиосоединения AirPods Pro 3 не присылают ни одного пакета 0x09: нет ни текущего режима, ни эха.
При этом команды **применяются** (пользователь проверил на слух). Поэтому итог `sent`: команда доставлена по каналу, где handshake подтверждён.
В C# это `NoiseControlOutcome.Sent`: `Success = true`, `Confirmed = false`. В интерфейсе: «sent HH:mm (not confirmed by AirPods)».
Опция `--a2dp` (bluez-alsa как A2DP-источник) в эксперименте давала полный статус и эхо, но стоит +4–5 с и в рабочем пути
воспроизводится нестабильно, поэтому выключена по умолчанию.

### 5a. Скрипты v3: постоянный демон (14.11.2026)
- Переключение режимов идёт через `bridge/linux/aapd.py`. Это долгоживущий процесс `wsl.exe … python3 aapd.py`,
  обмен JSON-строками через stdin/stdout (`BridgeDaemonClient.cs`). Операции: `ping`, `switch`, `release`, `quit`.
- bluetoothd и dbus не нужны. Ключ из BlueZ-файла `info` загружается в ядро через management-сокет
  (`LOAD_LINK_KEYS` 0x0012), затем `SET_POWERED` 0x0005. Запасной путь — `bridge.sh bluez-up/bluez-down`.
- Перед detach: `release` (закрыть канал, `MGMT_OP_DISCONNECT` 0x0014). Attach повторяется при «busy».
  Очередь «последний запрос» (`AirPodsController.QueueModeWhileSwitching`, `WslBridgeProvider.NextRequest`).
  После возврата адаптера `KickConnectionAsync` (SDP-запрос без кэша) ускоряет переподключение.
- Измерено: передача адаптера 8,4–9,0 с вместо 14,6 с, возврат AirPods к Windows 1,8–2,5 с вместо ≤13 с,
  второй режим в той же передаче — 0,2 с. Путь `bridge.sh run` остался для тестов мастера и как запасной (`--legacy`).

### 5b. Скрипты v4: координатор, трассы, заряд, защита ввода (14.11.2026)
- `Core/Bridge/HandoverCoordinator.cs`: каждая передача адаптера одна за раз, в том числе между процессами
  (`operation.lock`). Проверки (адаптер, устройства ввода) выполняются до выключения радио. Возврат в `finally`. Повторов нет.
  Нажатия во время переключения объединяются (`TryMerge`).
- Трасса `OperationTrace`: ID операции, инициатор, события App/Windows/Daemon/Linux/Aap. Файл `logs/bridge-trace-*.log`.
  Измерено: внутри одной операции звук пропадает один раз; AirPods дважды меняют связь (Windows → Linux → Windows).
  Лишних повторов нет. «Два-три отключения» давали отдельные операции подряд и повторы в старом пути.
- Заряд: «Refresh battery» (BLE, без прерывания, честно сообщает, если нового пакета нет) и «Exact (AAP)…»
  (отдельное действие с предупреждением, операция демона `read`). Устаревшие значения хранятся со временем получения.
- Защита ввода: `InputDeviceScanner` (дерево устройств + WinRT ConnectionStatus). Блокирует, если подключены мышь
  или клавиатура, а также если состояние неизвестно. Обход — диалог (по умолчанию «Нет») или `--allow-input-loss`.
  Радио выключается только у этого адаптера (`AdapterMatcher`, `BluetoothRadioSwitch` по пути интерфейса).
- Режимы удержания ножки (0x1A, + 0x34 при Off; 0x16 не трогаем): Settings → Press and hold → Apply to AirPods,
  CLI `bridge-hold-cycle` / `bridge-enable-off`. Pro 3 через мост не сообщают текущий набор — он «unknown».
  Применение проверено на слух (наборы 0x07, 0x09, 0x05); сохранение после переподключения и кейса — нет (BRIDGE-OPERATIONS §5a).
- `AirPodsControl.exe --exit` штатно закрывает запущенный экземпляр (ждёт конца передачи адаптера).
- Подробности: `docs/BRIDGE-OPERATIONS.md`.

## 6. Ядро WSL для MediaTek
Стоковое ядро WSL 6.6.114.1 собрано без `CONFIG_BT_HCIBTUSB_MTK`. Поэтому собрано своё ядро (`kernel/build-kernel.sh`, ~33 мин на 12 потоках):
- тот же тег Microsoft `linux-msft-wsl-6.6.114.1` и тот же `Microsoft/config-wsl`, плюс `BT_HCIBTUSB_MTK=y`;
- патч `kernel/patches/0001-btusb-add-13d3-3622-mt7922.patch` (ID 13d3:3622 не было в таблице btusb);
- прошивки MT7921/7922/7925 встроены (`CONFIG_EXTRA_FIRMWARE`): ядро не видит `/lib/firmware` дистрибутива;
- модули лежат в `modules.vhdx` (USB, usbip и BT в стоковой конфигурации — модули). Версия: `6.6.114.1-microsoft-standard-WSL2-btmtk+`.
- Установка (`WslKernelManager`): копия в `%LOCALAPPDATA%\AirPodsControl\kernel\`, бэкап `.wslconfig`, в секцию `[wsl2]`
  дописываются маркер, `kernel=` и `kernelModules=`. Для `modules.vhdx` выдаётся право чтения `NT VIRTUAL MACHINE\Virtual Machines`
  (S-1-5-83-0), иначе `HCS/E_ACCESSDENIED`. Затем `wsl --shutdown`. Чужой `kernel=` не перезаписывается. Удаление убирает только свои строки.
- Ядро общее для всех дистрибутивов WSL, включая Docker Desktop: docker-desktop на нём стартует.

## 7. Мастер настройки (одна кнопка, один UAC)
Шаги: WSL → поиск USB-адаптера (по ClassGUID Bluetooth; MediaTek — по точному списку ID из btusb, `MediaTekUsbIds.g.cs`) →
Alpine minirootfs (проверка SHA-256) → `apk add bluez bluez-btmgmt dbus python3` и копирование скриптов → ядро MediaTek
(только с согласия; также предлагается, если тест вернул `adapter_not_ready`) → **UAC**: установка usbipd-win через winget,
`usbipd bind --hardware-id`, экспорт ключа сопряжения только этих AirPods (одноразовая задача планировщика от SYSTEM читает
`HKLM\…\BTHPORT\Parameters\Keys`, ключ уходит в `/var/lib/bluetooth/<adapter>/<device>/info` через stdin, файл на стороне Windows затирается)
→ тест адаптера с LE-сканом → тест AirPods (AAP, только чтение). «Remove driverless mode» удаляет дистрибутив вместе с ключом,
выполняет unbind и удаляет строки ядра из `.wslconfig`.

## 8. Код (папка проекта)
- `src/AirPodsControl.Core` — протокол (AapPackets, AapParser, ProximityPairing), модели, NoiseControlManager, BatteryMonitor,
  `ModeSwitching.cs` (IModeSwitchProvider, BackendSelector: Auto / WslBridge / MagicAap), `Bridge/` (парсеры usbipd, ключей, RESULT; WslConfigFile; MediaTekUsbIds).
- `src/AirPodsControl.Windows` — BluetoothDeviceManager, BleAdvertisementSource, AirPodsController (передача адаптера, переподключение),
  `Providers.cs` (MagicAapProvider, WslBridgeProvider), `Bridge/` (ProcessRunner, UsbipdClient/WslClient, BridgeConfig, BridgeSetupService,
  WslKernelManager, BluetoothRadioSwitch).
- `src/AirPodsControl.App` — WPF: MainWindow (панель), SettingsWindow (выбор бэкенда), BridgeSetupWindow (мастер), трей, горячие клавиши.
- `src/AirPodsControl.Cli` — `airpodsctl`: diag, battery, mode, monitor, bridge-status, bridge-setup [--yes], bridge-provision,
  bridge-adapter-test, bridge-read [--trace], bridge-mode <mode> [--trace --a2dp], kernel-status, kernel-install [--from DIR], kernel-remove.
- `bridge/linux/aap_bridge.py` (только stdlib), `bridge/linux/bridge.sh`, `bridge/windows/elevated-setup.ps1` — встроены в DLL как ресурсы.
- `kernel/` — сборка ядра, патчи, генератор ID. `tests/` — 154 xUnit + 61 Python. `build.ps1` — тесты, self-contained single-file сборка,
  zip (вместе с `wsl-kernel\`). Inno Setup необязателен.
- Документация: `docs/WSL-BRIDGE.md` (полная история и трассы), `docs/PROTOCOL.md`, `docs/WINDOWS-DRIVER-RESEARCH.md`.

## 9. Файлы состояния
- `%APPDATA%\AirPodsControl\settings.json` — настройки, в т.ч. `ControlBackend`.
- `%LOCALAPPDATA%\AirPodsControl\bridge\bridge.json` — состояние моста (ключей там нет).
- `%LOCALAPPDATA%\AirPodsControl\logs` — логи (7 дней).
- `%UserProfile%\.wslconfig` — строки ядра с маркером AirPods Control.

## 10. Проверено вживую / не проверено
Проверено:
- заряд по BLE; полный мастер (34 с);
- hci0 в WSL и LE-скан, AirPods Pro 3 видны;
- AAP через мост: точный заряд, «в ухе»;
- переключение режимов через мост (на слух, ~15 с, итог `sent`) и переподключение AirPods к Windows;
- демон v3 через CLI: 8,4–9,0 с, возврат к Windows 1,8–2,5 с (на слух подтверждено пользователем: Прозрачность → Шумоподавление).

Не проверено:
- очередь «последний запрос» в программе (горячие клавиши через демон проверены, очередь — только через CLI);
- MagicAAP (заблокирован);
- адаптеры Intel/Realtek/Broadcom;
- Docker Engine на новом ядре (проверен только старт дистрибутива).

## 11. Ограничения и правила пользователя
- Не отключать Secure Boot, не включать Test Mode, не менять параметры безопасности Windows.
- Права администратора нужны только для одного шага мастера.
- Не утверждать, что что-то проверено на железе, если это не проверялось. Не выдавать отправку за подтверждение.
- Android-версия отложена по просьбе пользователя. Изменения Driverless Mode пока не закоммичены в git.

# Driverless Mode: WSL2 + usbipd

Альтернативный способ переключать ANC / Off / Transparency / Adaptive **без Test Mode, без отключения Secure Boot
и без драйвера ядра**. Индикация заряда по BLE и весь интерфейс остаются прежними.

> Статус (14.11.2026): на тестовом ПК (MediaTek MT7922, 13d3:3622, ядро AirPods Control) **проверено вживую**:
> радио выкл → attach → hci0 → BlueZ powered → LE-скан видит AirPods Pro 3 (0x2027) → detach → радио вкл → AirPods снова подключены к Windows.
> С ключом сопряжения из Windows **канал L2CAP 0x1001 открывается, AAP-handshake проходит, AirPods присылают заряд и статус «в ухе»**.
> **Переключение режимов работает**: пользователь на слух подтвердил, что ANC / Off / Transparency / Adaptive реально меняются.
> Но без аудиосоединения AirPods Pro 3 **не присылают эхо** (ни одного пакета 0x09), поэтому мост сообщает `sent`, а не `confirmed`.
> В приложении режим подписан как «sent HH:mm (not confirmed by AirPods)». Один переключатель занимает около 15 с. См. §7.

## 1. Как это работает

```
Windows (AirPods Control)                          WSL2: дистрибутив "airpods-bridge" (Alpine 3.24)
 1. usbipd state → BUSID адаптера (по VID:PID)
 2. wsl --exec sleep 120   (держит VM запущенной)
 3. радио Bluetooth Windows → Off (WinRT Radio API, без админа)
    usbipd attach --wsl airpods-bridge --busid X ─▶  btusb → hci0 (Windows в этот момент без Bluetooth)
 4. wsl --exec /opt/airpods/bridge.sh run ...  ───▶  ждёт hci0 (по умолчанию 4 с) → dbus + bluetoothd
                                                     (без авто-переподключения профилей) → btmgmt power on
                                                     → aap_bridge.py: L2CAP PSM 0x1001, handshake,
                                                       0x34 (для Off), 0x0D <mode>, ждёт эхо
                                              ◀───  RESULT {"ok":…, "outcome":"confirmed|different|no_response|…"}
 5. finally: usbipd detach --busid X               (адаптер возвращается Windows всегда, даже при ошибке)
            радио → On, когда адаптер снова появится в Windows (до 15 с)
 6. AirPods Control заново находит радиомодуль, перезапускает BLE и ждёт переподключения AirPods (до 20 с)
```

**Почему выключается радио.** Пока радио Bluetooth в Windows включено, стек Windows не отдаёт адаптер.
usbipd отвечает `Attach Request … failed - Device busy (exported)` и предлагает `bind --force`. Этот вариант не используется:
он отбирает адаптер у Windows насовсем, до `unbind`. С выключенным радио (тот же переключатель, что в «Параметрах»)
attach проходит. Флаг `RadioTurnedOff` в `bridge.json` нужен на случай сбоя: при следующем запуске приложение включит радио.

* Все вызовы `usbipd.exe` и `wsl.exe` идут с `CREATE_NO_WINDOW` (`ProcessRunner`), асинхронно и с тайм-аутами.
  Дочерние процессы помещаются в Job Object с `KILL_ON_JOB_CLOSE`.
* Если приложение упало, пока адаптер был в WSL, при следующем запуске `WslBridgeProvider.RecoverAsync` выполнит `detach`.
  Вручную: `usbipd detach --all`.
* Режим считается установленным, только если AirPods вернули тот же 0x0D. Логика та же, что для MagicAAP.
* Байты пакетов идентичны `AapPackets.cs`. Это проверяют `tests/bridge/test_aap_bridge.py` и C#-тест `BridgeScriptSyncTests`
  (сравнивает вывод `aap_bridge.py --selftest`).

### Выбор бэкенда (Strategy)
`IModeSwitchProvider` → `MagicAapProvider` (постоянный канал драйвера) и `WslBridgeProvider` (сеанс на одну команду).
В настройках (шестерёнка) — **Auto** / **WSL2 Bridge (No Test Mode)** / **MagicAAP Driver**.
Auto предпочитает MagicAAP, если его канал открыт (звук не прерывается), иначе использует мост.

## 2. Цена: прерывание звука
Пока адаптер находится в Linux, у Windows нет Bluetooth. Звук на AirPods прерывается, и они переподключаются.
Обещанные «2–3 секунды» нереалистичны. Оценка (не измерена на AirPods): attach ≈1–2 с, запуск адаптера и BlueZ ≈1–3 с,
L2CAP-подключение ≈1–3 с (AirPods могут отклонять первые попытки, пока считают связь с Windows живой; скрипт повторяет до 12 с),
detach и повторная инициализация в Windows ≈2–5 с, переподключение аудио ≈2–10 с. **Итого порядка 10–20 с.**
**Измерено (тест адаптера с 8-секундным LE-сканом, без L2CAP):** операция заняла 17 с: радио выкл → attach ≈3,6 с,
hci0 через ≈0,1 с, BlueZ powered ≈2 с, скан 8 с, detach + радио вкл ≈2 с. AirPods снова были подключены к Windows
не позже чем через 13 с после возврата адаптера. Без скана, но с L2CAP, ожидается сопоставимое время. Это ещё предстоит измерить.
Если AirPods не вернутся сами, приложение так и сообщит: нужно открыть кейс или подключить их в параметрах Bluetooth.
Режим на AirPods после переподключения сохраняется (это состояние наушников, а не Windows), но это тоже требует проверки.

### Постоянный демон (скрипты v3, 14.11.2026)
С одним адаптером оба обрыва неизбежны: пока USB-устройство передано в WSL, у Windows нет Bluetooth.
`btmgmt power off` адаптер Windows не возвращает. Поэтому оптимизирован сам цикл:

- **`aapd.py`.** Процесс `wsl.exe … python3 /opt/airpods/aapd.py` запускается один раз и общается с приложением
  JSON-строками через stdin/stdout. Портов, pipe и пакетов нет, процесс сидит в Job Object приложения. Он же держит VM
  запущенной (вместо `sleep 120`) и сам завершается после 15 мин простоя (`DaemonIdleMinutes`).
- **Без bluetoothd и dbus.** Ключ из `/var/lib/bluetooth/<адаптер>/<устройство>/info` загружается в ядро через
  management-сокет (`MGMT_OP_LOAD_LINK_KEYS`, как это делает сам bluetoothd), затем `SET_POWERED`.
  Если это не удалось, остаётся запасной путь `bridge.sh bluez-up`.
- **Эхо не ждём.** `confirm_timeout` 0,2 с (без A2DP эха всё равно нет), settle 0,5 с, чтобы собрать заряд.
- **Перед detach выполняется `release`.** Демон закрывает канал AAP и рвёт ACL (`MGMT_OP_DISCONNECT`): AirPods сразу
  узнают, что этот хост ушёл.
- **Attach с повторами.** Фиксированной паузы 1 с после выключения радио больше нет: при «busy» attach повторяется
  каждые 250 мс, максимум 8 с.
- **Очередь «последний запрос».** Горячие клавиши и меню трея во время переключения не теряются. Если адаптер ещё в WSL,
  следующий режим уходит по уже открытому каналу (0,2 с). Если адаптер уже вернулся, выполняется ещё один цикл.
- **Подталкивание переподключения.** Когда радио снова включено, `GetRfcommServicesAsync(Uncached)` заставляет Windows
  саму открыть связь с AirPods (`ReconnectKick`).
- **Обновление скриптов.** Новые скрипты копируются в дистрибутив автоматически, без мастера и без прав администратора.

**Измерено (AirPods Pro 3, MT7922, CLI `bridge-mode`):**

| Этап | Было (v2) | Стало (v3) |
|---|---|---|
| Готовность WSL / демона | wsl.exe + `true` | 0,6–1,0 с (холодный старт демона; в приложении он уже запущен) |
| Радио выкл → attach | 1 с пауза + ≈3 с | ≈3 с (сам `usbipd attach`) |
| hci → питание | dbus + bluetoothd + повторы ≈2 с и больше | 0,53 → 0,53 с (одна попытка) |
| L2CAP + handshake + отправка | ≈3–4 с + 1,5 с ожидания эха | 1,9–2,9 с |
| Второй режим в той же передаче | новый цикл | 0,2 с |
| release + detach + радио вкл | ≈2 с | ≈2,1 с |
| **Передача адаптера целиком** | **14,6 с** | **8,4–9,0 с** |
| AirPods снова подключены к Windows | до 13 с | **1,8–2,5 с** (подталкивание сработало) |

Режим применяется (`sent`, подтверждения нет, см. §7). На слух v3 проверен пользователем: после `transparency,anc` включилось шумоподавление.

**Программа, горячие клавиши (три переключения подряд):** передача адаптера 12,0 с (холодный старт VM и демона 3,6 с),
затем 9,6 и 8,1 с. Звука нет ≈11 с на одно переключение. Возврат к Windows через 2,0, 2,2 и 5,0 с.
В последнем случае подталкивание не запустилось из-за устаревшего состояния связи, исправлено: теперь оно запускается всегда.
Очередь в программе ещё не проверена: нажатия пришли уже после окончания цикла (через CLI очередь проверена).

### Операции, трассы, заряд, защита устройств ввода (скрипты v4)
См. [BRIDGE-OPERATIONS.md](BRIDGE-OPERATIONS.md): одна операция — один цикл, межпроцессная блокировка, трассы (причина
двух потерь связи AirPods), BLE- и AAP-обновление заряда, защита мыши и клавиатуры, точный выбор радио, второй адаптер.

## 3. Мастер настройки (одна кнопка)
Кнопка **Set up driverless mode (WSL2)** находится на нижней плашке главного окна или в Settings → Driverless setup.
1. Проверка WSL 2 (`wsl --version`). Если WSL нет, установку (`wsl --install`, админ + перезагрузка) мастер не делает.
2. Поиск USB-адаптера Bluetooth (класс Bluetooth в `HKLM\SYSTEM\CurrentControlSet\Enum\USB`). Для MediaTek показывается предупреждение.
3. Загрузка Alpine minirootfs (`latest-releases.yaml` → файл и SHA-256; запасной вариант — `.sha256`), проверка хеша и `wsl --import airpods-bridge`.
4. `apk add bluez bluez-btmgmt dbus python3` (+ `linux-firmware-mediatek` для MediaTek) и копирование `aap_bridge.py` и `bridge.sh`.
5. **Один запрос UAC** (`bridge/windows/elevated-setup.ps1`):
   * установка usbipd-win через winget, если его нет (`dorssel.usbipd-win`, GPL-3.0);
   * `usbipd bind --hardware-id VID:PID`. Windows продолжает пользоваться адаптером до `attach`; после bind права администратора для attach/detach не нужны;
   * **копирование ключа сопряжения только этих AirPods.** Windows хранит ключи BR/EDR в
     `HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Keys` с доступом только для SYSTEM. Поэтому одноразовая задача планировщика
     от SYSTEM экспортирует ветку во временный файл в `C:\Windows\Temp`. Скрипт оставляет только значение нужного MAC, остальное удаляет
     вместе с задачей. Приложение записывает ключ в `/var/lib/bluetooth/<adapter>/<device>/info` внутри дистрибутива (через stdin,
     не в аргументах) и затирает файл на стороне Windows.
     *Зачем:* адаптер в Linux имеет тот же BD_ADDR, что и в Windows. С тем же ключом AirPods принимают его как уже сопряжённое устройство.
     Повторное сопряжение из Linux создало бы новый ключ и **сломало бы пару с Windows**.
6. Тест адаптера: attach → hci0 → BlueZ → detach.
7. Тест AirPods (только чтение): подключение по L2CAP 0x1001, текущий режим и точный заряд.

**Remove driverless mode** выполняет `wsl --unregister airpods-bridge` (дистрибутив удаляется вместе с ключом) и `usbipd unbind` (UAC).

## 4. Совместимость адаптеров — главное ограничение
Ядро WSL 6.6.114.1 (`microsoft/WSL2-Linux-Kernel`, `arch/x86/configs/config-wsl`):
`CONFIG_BT=m`, `CONFIG_BT_HCIBTUSB=m`, `CONFIG_BT_INTEL=m`, `CONFIG_BT_RTL=m`, `CONFIG_BT_BCM=m`, но
**`# CONFIG_BT_HCIBTUSB_MTK is not set`**. В `btusb.c` MediaTek-инициализация (загрузка прошивки) включается только при
`IS_ENABLED(CONFIG_BT_HCIBTUSB_MTK)`. На тестовом ПК `btusb.ko` есть, а `btmtk.ko` нет.

| Адаптер | Стандартное ядро WSL | Ожидание |
|---|---|---|
| Intel (VID 8087) | btusb + btintel | должен работать (не проверено) |
| Realtek | btusb + btrtl | должен работать (не проверено), нужна прошивка `linux-firmware-rtl_bt` |
| Broadcom | btusb + btbcm | должен работать (не проверено) |
| **MediaTek (MT7921/MT7922/MT7925, на этом ПК 13D3:3622)** | **нет поддержки** | нужно ядро AirPods Control; с ним MT7922 **проверен**: hci0 за ~0,1 с, BlueZ, LE-скан |
| Не USB (UART/SDIO) | usbipd не может пробросить | не поддерживается |

### Ядро AirPods Control для MediaTek (`kernel/`)
Мастер ставит его сам, если адаптер MediaTek (или если тест адаптера вернул `adapter_not_ready`), но **только после
подтверждения**: ядро общее для **всех** дистрибутивов WSL, включая Docker Desktop, а `wsl --shutdown` останавливает их.

Что это за ядро (`kernel/build-kernel.sh`, собирается в одноразовом Alpine-дистрибутиве):
* исходники Microsoft `WSL2-Linux-Kernel`, тот же тег, что у стокового ядра (`linux-msft-wsl-6.6.114.1`), и тот же `Microsoft/config-wsl`;
* изменения конфигурации: `CONFIG_BT_HCIBTUSB_MTK=y` (и, как следствие, `CONFIG_BT_MTK=m`), `CONFIG_FW_LOADER_COMPRESS_ZSTD=y`,
  `LOCALVERSION=-microsoft-standard-WSL2-btmtk` (по суффиксу видно, что работает наше ядро). USB, usbip и Bluetooth
  в стоковой конфигурации собраны модулями (`CONFIG_USB=m`), поэтому так и остаётся: модули лежат в `modules.vhdx`
  (`Microsoft/scripts/gen_modules_vhdx.sh`), WSL подключает его через `kernelModules=`;
* патч `kernel/patches/0001-btusb-add-13d3-3622-mt7922.patch`: в таблице btusb ядра 6.6 нет ID `13d3:3622` (MT7922 на тестовом ПК).
  Без записи btusb привязывается по классу устройства и не запускает инициализацию MediaTek;
* прошивки `BT_RAM_CODE_MT7922_1_1`, `MT7961_1_2` (MT7921), `mt7925/…MT7925_1_1` встроены в образ (`CONFIG_EXTRA_FIRMWARE`).
  Загрузчик прошивок ядра ищет файлы в корневом пространстве имён (служебная VM WSL), а не в `/lib/firmware` дистрибутива,
  поэтому пакет прошивок внутри Alpine может быть не виден ядру. Встроенные прошивки этот вопрос снимают.
* Список MediaTek-ID для мастера (`MediaTekUsbIds.g.cs`) генерируется из той же таблицы btusb (`kernel/gen-mediatek-ids.py`).
  VID `13d3` / `0489` сам по себе ничего не говорит: эти производители выпускают и Realtek-модули.

Установка (её делает мастер или `airpodsctl kernel-install`): файлы копируются в `%LOCALAPPDATA%\AirPodsControl\kernel`
(SHA-256 проверяется), старый `.wslconfig` сохраняется как `.wslconfig.airpodscontrol-<дата>.bak`, в секцию `[wsl2]` добавляются
три строки (комментарий-маркер, `kernel=…`, `kernelModules=…`), затем выполняется `wsl --shutdown`.
`modules.vhdx` получает право чтения для группы `NT VIRTUAL MACHINE\Virtual Machines` (S-1-5-83-0). VHD открывает рабочий
процесс VM, а не пользователь, и без этого права WSL на тестовом ПК не запускался: `HCS/E_ACCESSDENIED`. Если в `.wslconfig` уже есть
**чужой** `kernel=`, он не перезаписывается. **Remove driverless mode** / `airpodsctl kernel-remove` удаляют только эти строки
(снова с бэкапом), пустую секцию `[wsl2]` и сам файл, если он стал пустым, затем `wsl --shutdown`.

Сборка вручную (`kernel/build-kernel.sh <out-dir>` от root в Alpine WSL) даёт `bzImage`, `modules.vhdx`, `config`, `BUILDINFO`, `SHA256SUMS`.
`build.ps1` кладёт содержимое `assets/wsl-kernel/` (в git не попадает) в `wsl-kernel\` релизного архива вместе со скриптом сборки и патчами.
Лицензии: ядро — GPL-2.0, точный коммит исходников указан в `BUILDINFO`. Прошивки MediaTek взяты из linux-firmware и распространяются по их лицензии.

## 5. Что проверено
| Проверка | Результат |
|---|---|
| Python-тесты моста (сериализация, разбор, подтверждение и отказ, тайм-аут, обрыв, повторы подключения) | 15/15 ✅ |
| C#-тесты (парсеры usbipd/реестра/RESULT, выбор бэкенда, синхронизация байтов Python↔C#) | все ✅ |
| Загрузка Alpine 3.24.4 + SHA-256 + `wsl --import` + `apk add` на этом ПК (временный дистрибутив, затем удалён) | ✅ |
| Синтаксис `bridge.sh`, `aap_bridge.py --selftest` внутри Alpine; Python 3.14 в Alpine поддерживает `AF_BLUETOOTH` | ✅ |
| В ядре WSL есть `btusb.ko`, Bluetooth-стек инициализируется; при отсутствии адаптера `bridge.sh run` возвращает корректный `adapter_not_ready` | ✅ |
| Сборка ядра (`build-kernel.sh`, 12 потоков) | ✅ 33 мин; `bzImage` 17,5 МБ, `modules.vhdx` 240 МБ |
| WSL на ядре `6.6.114.1-microsoft-standard-WSL2-btmtk+`, модули `btusb`, `btmtk`, `vhci_hcd` грузятся из `modules.vhdx`; дистрибутив docker-desktop стартует | ✅ |
| Установка usbipd-win 5.3.0 (winget) + `bind --hardware-id 13d3:3622` (один UAC) | ✅ |
| attach при включённом радио Windows | ❌ `Device busy (exported)`, поэтому радио выключается на время сеанса |
| `airpodsctl bridge-adapter-test`: радио off → attach → hci0 → BlueZ powered → LE-скан → detach → радио on | ✅ 17 с, адрес `50:2E:91:F7:60:E5`, 10 BLE-устройств, **AirPods Pro 3 (0x2027) видны**, RSSI −55 |
| AirPods снова подключены к Windows после теста | ✅ `Connected: True` через ≤13 с |
| Полный мастер (`airpodsctl bridge-setup`): один UAC, bind, экспорт ключа только этих AirPods, `keys.reg` удалён | ✅ 34 с |
| L2CAP 0x1001 с ключом из Windows (BlueZ, `BT_SECURITY_MEDIUM`), AAP-handshake | ✅ ответ `01 00 04 00 …` |
| Уведомления после handshake | ✅ при строгом порядке (handshake → ждать ответ → 0x4D → 0x0F): заряд L 68 % / R 67 %, оба в ухе; ❌ если слать всё сразу через 0,1 с |
| Переключение Transparency / ANC / Off / Adaptive через мост (без A2DP) | ✅ режимы меняются (подтверждено пользователем на слух); эха нет, итог `sent`, 14,6 с на переключение |
| То же с A2DP (`--a2dp`, bluez-alsa, `ConnectProfile`) | ✅ в эксперименте: AirPods прислали полный набор 0x09 (`0D 03`), команда `0D 02` получила эхо `0D 02`; +4–5 с; в рабочем пути воспроизводится не всегда (§7) |
| Восстановление радио Windows после прерванного сеанса | ✅ приложение: флаг `RadioTurnedOff`; ⚠️ мой тестовый скрипт без флага оставил радио выключенным, пришлось включить вручную |

## 6. Процедура проверки на оборудовании
1. `airpodsctl bridge-status`: показывает WSL, usbipd, адаптер, состояние настройки.
2. В приложении: **Set up driverless mode** → **Run setup** (UAC один раз).
3. Шаг «Adapter starts inside WSL» должен стать ✓. Для MediaTek без своего ядра ожидается ✗ с `adapter_not_ready`.
4. Шаг «AirPods answer» должен показать текущий режим и заряд с точностью 1 %.
5. `airpodsctl bridge-mode anc`, `transparency`, `off`: ожидается `Result: Confirmed`; заметьте, сколько секунд нет звука.
6. Проверьте, что после каждой команды AirPods снова подключаются к Windows и звук возвращается.
7. Логи: `%LOCALAPPDATA%\AirPodsControl\logs` (строки `usbipd …`, `bridge result: {...}`, `WSL bridge operation took …s`).
8. Трассировка пакетов: `airpodsctl bridge-read --settle 6 --trace`, `airpodsctl bridge-mode anc --trace [--own] [--features ff]`.

## 7. Почему нет подтверждения режима (AirPods Pro 3)
**Итог:** команды `09 00 0D xx` применяются (проверено пользователем на слух), но хосту без аудиосоединения AirPods не отправляют
пакеты 0x09: ни текущий режим, ни эхо. Поэтому `aap_bridge.py` возвращает `sent`, если handshake подтверждён и команда записана
в канал без ошибок. `no_response` остаётся, только если AirPods не ответили даже на handshake. C#: `NoiseControlOutcome.Sent`
считается успехом (`Success`), но не подтверждением (`Confirmed == false`). Интерфейс так и пишет: «sent, not confirmed».

С A2DP (bluez-alsa как источник звука, `bridge.sh run --a2dp`, пакет ставится через `bridge.sh provision --a2dp`) AirPods
в эксперименте прислали всё состояние (около 25 пакетов 0x09) и эхо на команду. Цена: плюс 4–5 с. В рабочем пути через
`ConnectProfile` полный набор не всегда приходил (скорее всего, важны пауза после A2DP или старт потока), поэтому по умолчанию A2DP выключен.

Протокол первой попытки, без A2DP:
Трасса сеанса на тестовом ПК (14.11.2026, AirPods Pro 3, оба в ухе):
```
tx 00 00 04 00 01 00 02 00 …            handshake
rx 01 00 04 00 00 00 01 00 03 00 …      ответ на handshake
tx 04 00 04 00 4D 00 FF 00 …            set features
tx 04 00 04 00 0F 00 FF FF FF FF        request notifications
tx 04 00 04 00 09 00 06 01 00 00 00     owns connection = 1 (эксперимент)
rx … 2B 00 …                            «paired devices (?)» (LibrePods)
rx … 2E 00 01 00 01 50 2E 91 F7 60 E5 01 06   «list of connected devices»: наш адаптер, затем … 02 06
rx … 04 00 03 04 01 42 02 01 02 01 42 02 01 08 01 00 04 01   заряд: R 66 %, L 66 %, кейс отключён
rx … 0E 00 E5 60 F7 91 2E 50 00         «audio source resp»: наш адрес, флаг 00
rx … 06 00 00 00                        ear detection: оба в ухе
tx 04 00 04 00 09 00 0D 03 00 00 00     listening mode = Transparency → ответа нет
```
По документации LibrePods после 0x0F AirPods должны прислать хотя бы статус Conversational Awareness (`09 00 28 xx`).
Без A2DP не приходит ни одного пакета 0x09, но Transparency при этом включилась: пользователь это слышал.

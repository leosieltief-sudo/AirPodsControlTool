# Протокол AirPods и доступ из Windows: результаты исследования

Все байтовые последовательности в коде взяты из открытых реализаций, перечисленных ниже. Ничего не придумано.
Если источники расходятся, это указано явно.

## 1. Два канала данных

| Канал | Транспорт | Что даёт | Доступ из Windows (user mode) |
|---|---|---|---|
| **AAP / AACP** (Apple Accessory Protocol) | Classic BT, **L2CAP PSM 0x1001**, SDP UUID `74ec2172-0bad-4d01-8f77-997b2be0722a` | режимы ANC/Transparency/Off/Adaptive, заряд с точностью 1 %, датчики уха, модель, прошивка | **нет**, нужен kernel-mode profile driver |
| **Proximity Pairing** (Continuity) | BLE advertisement, manufacturer data `0x004C`, тип `0x07` | заряд с шагом 10 %, зарядка, крышка кейса, в ухе, ID модели | **да**, WinRT `BluetoothLEAdvertisementWatcher` |

### Почему без драйвера нельзя (проверено на этом ПК)
* Winsock `AF_BTH` поддерживает только RFCOMM. Вызов `socket(AF_BTH, SOCK_SEQPACKET, BTHPROTO_L2CAP)` на тестовой машине
  (Windows 11 26200) вернул **`WSAESOCKTNOSUPPORT (10044)`**. Это повторяется командой `airpodsctl diag`.
* В WinRT нет API для L2CAP поверх BR/EDR. `RfcommDeviceService` не подходит: AAP работает не через RFCOMM.
* В стеке Microsoft L2CAP-каналы открывает только kernel-mode *profile driver* через BRB (`BRB_L2CA_OPEN_CHANNEL`).
  Windows создаёт для AirPods узел `BTHENUM\{74ec2172-…}_VID&0001004c_PID&xxxx` («AAP Server»), но драйвер к нему не привязан.
  На тестовом ПК: `AAP driver: (no driver bound)`.
* Единственный известный драйвер, который пробрасывает этот канал, это **MagicAAP** от автора MagicPods. По данным
  YimingZhanshen/AirPodsWindows (`Docs/Driver.md`), он не подписан для обычной загрузки: нужен Test Mode
  (`bcdedit /set testsigning on`) и выключенный Secure Boot.
  Альтернатива — свой KMDF profile driver. Он упирается в то же требование подписи (EV-сертификат + attestation signing у Microsoft),
  а WDK на машине сборки не установлен. Поэтому в проекте реализован **клиент** уже существующего драйвера,
  а не ещё один неподписанный драйвер.

### Интерфейс MagicAAP (источник: YimingZhanshen/AirPodsWindows `Source/Core/MagicAAPWinRT.cpp`)
* GUID интерфейса устройства: `{9EEC98BB-3C54-45D4-A843-7900C4635E08}`. В исходнике он помечен как «from driver analysis».
* Открытие: `CreateFileW(path, GENERIC_READ|GENERIC_WRITE, FILE_SHARE_READ|FILE_SHARE_WRITE, …, FILE_FLAG_OVERLAPPED)`.
* Обмен: «сырые» AAP-пакеты через `WriteFile`/`ReadFile`, без дополнительного кадрирования.
* Наша реализация (`DriverAapTransport`) выбирает интерфейс строго на узле AAP конкретных AirPods (`CM_Get_Device_Interface_List`
  с instance ID узла) или по MAC в пути. «Первое попавшееся устройство» никогда не открывается.
* **Статус: требуется проверка на оборудовании с установленным MagicAAP.**

## 2. Пакеты AAP (источник: librepods-org/librepods)

Файлы: `docs/AAP Definitions.md`, `docs/control_commands.md`, `docs/device-info.md`, `linux/airpods_packets.h`,
`android/.../AACPManager.kt`, `android/.../Packets.kt`.

| Назначение | Байты | Примечание |
|---|---|---|
| Handshake | `00 00 04 00 01 00 02 00 00 00 00 00 00 00 00 00` | без него AirPods не отвечают |
| Feature flags (0x4D) | `04 00 04 00 4D 00 D7 00 00 00 00 00 00 00` | так в коде LibrePods; в документации снят дамп с `FF` |
| Подписка на уведомления (0x0F) | `04 00 04 00 0F 00 FF FF FF FF` | вариант из документации («This packet also works»); `airpods_packets.h` использует 5×FF |
| Listening mode (ctrl 0x0D) | `04 00 04 00 09 00 0D [m] 00 00 00` | m: 01 Off, 02 ANC, 03 Transparency, 04 Adaptive. AirPods возвращают такой же пакет |
| Allow Off Option (ctrl 0x34) | `04 00 04 00 09 00 34 01 00 00 00` / `…34 02…` | 01 разрешить, 02 запретить |
| Listening-mode cycle (ctrl 0x1A) | `04 00 04 00 09 00 1A [mask] 00 00 00` | маска Off=1, ANC=2, Transp=4, Adaptive=8. **Приложение её не меняет** |
| Батарея (0x04) | `04 00 04 00 04 00 [n] ([comp] 01 [lvl] [status] 01)×n` | comp: 02 R, 04 L, 08 Case; status: 01 charging, 02 not charging, 04 disconnected, 05 optimized |
| Ear detection (0x06) | `04 00 04 00 06 00 [primary] [secondary]` | 00 in ear, 01 out, 02 in case |
| Metadata (0x1D) | `04 00 04 00 1D 00 ?? ?? ?? ?? ??` + строки с NUL | имя, номер модели (A3048 …), производитель, серийник, прошивка |

**Подтверждение режима.** Режим считается установленным, только когда от AirPods пришёл пакет `0x0D` с запрошенным значением.
Если ответа нет, результат `NoResponse` («состояние неизвестно»). Если пришло другое значение, результат `DeviceReportedDifferentMode`.

**Off на Pro 2 / Pro 3 / AirPods 4 ANC.** В iOS «Off» нужно отдельно разрешить (Settings → AirPods → Off Listening Mode).
В протоколе за это отвечает control `0x34`. Если опция «Allow 'Off' automatically» включена, приложение сначала отправляет `0x34 = 01`,
дожидается эха и только потом отправляет `0x0D = 01`. Как поведут себя AirPods, если Off запрещён, в источниках не описано.
Поэтому приложение показывает фактически подтверждённый режим, а не запрошенный.

## 3. BLE Proximity Pairing (источник: SpriteOvO/AirPodsDesktop `Source/Core/AppleCP.h/.cpp`)

Буфер начинается с байта типа (company ID WinRT передаёт отдельно):

```
0 type=0x07 | 1 len=25 | 2 ? | 3-4 model id (LE) | 5 flags | 6 bat: lo=curr hi=other | 7 lo=case, b4 curr chg, b5 other chg, b6 case chg | 8 lid (b3=closed) | 9 color | 10 ? | 11-26 encrypted
flags: b1 curr in ear, b2 both in case, b3 other in ear, b5 = 1 → рекламирует левый наушник
```
Значение 0..10 означает ×10 %, остальные значения — «нет данных» (показывается «—»).
Адреса BLE случайные, поэтому сопоставление идёт по ID модели и RSSI ≥ −72 dBm с выбором самого сильного сигнала (как в AirPodsDesktop).

## 4. Идентификация сопряжённых AirPods

Через CfgMgr32 (`CM_Get_Device_ID_List`, фильтр `BTHENUM`, без прав администратора) ищутся узлы
`BTHENUM\{uuid}_VID&0001004C_PID&xxxx\…&<MAC>_C…`. VID `0x004C` — Apple, PID — модель (таблица ниже). Имя наушников по названию не угадывается.

## 5. Таблица совместимости

| Модель | PID | ANC/Transp. | Off | Adaptive | Источник возможностей | Проверено на железе |
|---|---|---|---|---|---|---|
| AirPods 1/2/3 | 2002/200F/2013 | — | — | — | LibrePods AirPods.kt | — |
| AirPods 4 | 2019 | — | — | — | LibrePods | — |
| AirPods 4 (ANC) | 201B | да | через 0x34 | да | LibrePods | нет |
| AirPods Pro | 200E | да | нативно | — | LibrePods (LISTENING_MODE) | нет |
| AirPods Pro 2 (Lightning/USB-C) | 2014/2024 | да | через 0x34 | да | LibrePods (доки сняты с Pro 2 USB-C) | нет |
| AirPods Pro 3 | 2027 | да | через 0x34 (предп.) | да | LibrePods AirPods.kt | **BLE-батарея, обнаружение, подключение — да; режимы — нет (нет драйвера)** |
| AirPods Max / Max USB-C | 200A/201F | не проверено | нативно (предп.) | — | LibrePods enums.h (модель есть, возможностей нет) | нет |
| Beats Fit Pro | 2012 | не проверено | ? | — | AirPodsDesktop (только PID) | нет |

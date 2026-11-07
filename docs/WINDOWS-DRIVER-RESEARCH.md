# Windows: управляющий канал AirPods — драйверы и подписи (исследование 07.11.2026)

Статус: **переключение режимов на Windows не проверено на оборудовании.** Владелец решил пока не отключать
Secure Boot и не включать Test Mode на основной системе. Windows-версия работает в режиме «батарея + статус»
(BLE), кнопки режимов честно отключены с объяснением.

## 1. Почему нужен драйвер ядра
* AirPods управляются по AAP через L2CAP PSM 0x1001 (Classic BT). Подробности протокола — [PROTOCOL.md](PROTOCOL.md).
* Winsock `AF_BTH` + `BTHPROTO_L2CAP` на тестовом ПК: `WSAESOCKTNOSUPPORT (10044)` (воспроизводится `airpodsctl diag`).
* WinRT не имеет API L2CAP для BR/EDR. В стеке Microsoft L2CAP открывают только kernel-mode profile drivers (BRB).
* Windows создаёт для AirPods узел `BTHENUM\{74ec2172-0bad-4d01-8f77-997b2be0722a}_VID&0001004c_PID&2027…` («AAP Server»)
  без драйвера. BTHENUM регистрирует на этом узле интерфейс класса `{74ec2172-…}` (найдено в
  `HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses`), но без функционального драйвера он не даёт канала данных.

## 2. MagicAAP (автор MagicPods)
| Факт | Значение | Как получено |
|---|---|---|
| Поставщик в INF | «IP Maslov Alexander Yuryevich», версия 0.0.0.1 | `DEVPKEY_Device_DriverProvider`, на тестовом ПК |
| Служба | `MagicAAP`, kernel driver, demand start, `magicaap.inf_amd64_…\MagicAAP.sys` | реестр Services, событие 7045 |
| Привязка | драйвер ставится функциональным на узел «AAP Server» | `DEVPKEY_Device_Service` после установки |
| Подпись | не подписан для production; нужен `testsigning on` + Secure Boot off | CodeIntegrity 3004/3077, документация AirPodsWindows |
| Результат при Secure Boot on / Test Mode off | **код 39**, `Status 0xC000026C`, блок политиками `{784c4414-…}` (аудит) и `{8f9cb695-5d48-48d6-a329-7202b44607e3}` (enforce) | System 219, CodeIntegrity 3077 |
| Интерфейс устройства | `{9EEC98BB-3C54-45D4-A843-7900C4635E08}` согласно YimingZhanshen/AirPodsWindows («from driver analysis») — **не подтверждён**, т.к. драйвер так и не загрузился | — |
| Риск | Test Mode разрешает любые test-signed драйверы; античиты могут не запускаться; смена Secure Boot при BitLocker (`FVEBOOT` в параметрах загрузки) может вызвать запрос ключа восстановления | — |
| Не рекомендуется | «community»-скрипт `irm …/magicaap-community-v1.ps1 \| iex` с отключением Defender и `CiTool --remove-policy {8f9cb695…}` | AirPodsWindows `Docs/Driver.md` |

Откат: Диспетчер устройств → «AAP Server» → удалить устройство + драйвер; `bcdedit /set testsigning off`; включить Secure Boot.

**TODO, когда драйвер загрузится:** перечислить интерфейсы на узле AAP (DeviceClasses), при необходимости поправить
`AapControlChannel.MagicAapInterfaceGuid`, затем `airpodsctl monitor 15 -v` и `airpodsctl mode anc|transparency|off`.

## 3. KikuTune Bridge (kikutune.com) — оценка как альтернативы
| Вопрос | Ответ | Источник |
|---|---|---|
| Что это | Бесплатное приложение Microsoft Store (Windows 11) + отдельный бесплатный «Bridge»; оператор — частное лицо (Andrii Herasym, бренд KikuLabs) | kikutune.com, /privacy |
| Исходники | Закрыты: «Application source code is not published here» | github.com/DarrosHere/KikuTune |
| Транспорт | Не раскрыт (L2CAP/PSM/GATT не упоминаются); только «AAP driver». Ретрансляции через телефон нет: Bridge общается с приложением «on this PC over a private local channel» | /bridge, README |
| Драйвер ядра | Да: «Windows gives apps no way to do that, so it takes a driver» | /bridge |
| Подпись | «not signed by Microsoft, so Windows only loads it in Test Mode»; установщик Bridge «not code-signed» | /bridge |
| Test Mode / Secure Boot | Да / должен быть выключен вручную; BitLocker приостанавливается на одну перезагрузку | /bridge |
| CiTool / политики CI | Не упоминаются → неизвестно, загрузится ли при политике `{8f9cb695…}` | — |
| AirPods Pro 3 | Не названы ни на одной странице | — |
| Доказательства работы | Независимых обзоров/обсуждений не найдено; GIF в README помечен «not a live recording»; репозиторий 0★, 2 issue от автора; v1.0 от 14.09.2026, шумоподавление с 1.2 (октябрь 2026). Тег `no-kernel` противоречит описанию | GitHub, /changelog |

**Вывод:** по требованиям безопасности эквивалентен MagicAAP, по прозрачности хуже. Альтернативой без Test Mode не является.
Не устанавливался.

## 4. Production-подпись собственного драйвера (официальная процедура Microsoft)
| Вариант | Требования | Результат |
|---|---|---|
| Attestation signing | EV-сертификат подписи кода, аккаунт Hardware Dev Center / Partner Center (регистрация организации: данные компании или D-U-N-S, юридический контакт, Entra ID admin); HLK не нужен | Загружается на Windows 10/11 Desktop без Test Mode; не Server; не публикуется в Windows Update для retail; «For testing purposes only», «not Windows Certified» |
| WHCP (HLK) | То же + прохождение Hardware Lab Kit | Все версии Windows включая Server; возможна раздача через Windows Update |

Источники: learn.microsoft.com — driver-signing-offerings, code-signing-attestation, hardware-program-register, code-signing-reqs.

**Апрель 2026:** Microsoft убирает доверие к драйверам кросс-подписанной программы на Windows 11 24H2/25H2/26H1 и Server 2025:
«only kernel drivers that the Windows Hardware Compatibility Program (WHCP) have passed and been signed can be loaded by default»
(блог Windows IT Pro, 26.03.2026). Про attestation-подписанные драйверы заметка явно не говорит → **перед вложениями уточнить у Microsoft.**

**Оценка для проекта:** частному разработчику путь практически закрыт (юрлицо, EV-сертификат, Partner Center);
сам KMDF Bluetooth profile driver ещё нужно написать (WDK не установлен), это недели работы. Ни один найденный
проект (MagicAAP, KikuTune, WPods, AirPodsWindows) не имеет production-подписанного драйвера.

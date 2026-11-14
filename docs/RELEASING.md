# Публикация и релиз / Publishing and releases

*English summary:* run the privacy check below, build with `.\build.ps1 -NoKernel` unless the MediaTek firmware licence question
(section 2) is resolved, push the repository (no `dist/`, no `assets/wsl-kernel/`), attach the portable zip to a GitHub Release.

## 1. Перед публикацией: личные данные

В репозиторий не должны попасть адреса ваших устройств, ключи, логи, пути профиля.

```powershell
# адрес ваших AirPods в любом виде (подставьте свой, 12 hex-цифр) — должно быть пусто
git grep -n -i -E "<AABBCCDDEEFF>|<AA:BB:CC:DD:EE:FF>"
# имя пользователя / почта / путь профиля — должно быть пусто
git grep -n -i -E "C:\\\\Users\\\\|@gmail|LinkKey"
# что реально уйдёт в репозиторий: без dist/, bin/, obj/, assets/wsl-kernel/
git status --short
git ls-files | Select-String "dist/|/bin/|/obj/|wsl-kernel|\.vhdx|bzImage"
```

В тестах и примерах используется выдуманный адрес `A1:B2:C3:D4:E5:F6`. Скриншоты в `docs/` проверены: на
`driverless-setup.png` адрес заменён на `AA:BB:CC:DD:EE:FF`. Новые скриншоты окна настройки мастера показывают реальный адрес —
маскируйте его перед коммитом.

## 2. Ядро WSL и лицензия прошивки MediaTek — нерешённый вопрос

Ядро AirPods Control содержит прошивку MediaTek **внутри** `bzImage` (`CONFIG_EXTRA_FIRMWARE`): ядро WSL не читает
`/lib/firmware` дистрибутива, поэтому иначе адаптер в WSL не стартует.

Лицензия прошивки ([kernel/LICENCE.mediatek](../kernel/LICENCE.mediatek), из linux-firmware) разрешает распространение
«for the use with devices containing MediaTek chipsets, but **not as part of the Linux kernel**…». Распространение
`bzImage` со встроенной прошивкой под это ограничение, по всей видимости, попадает. Это не юридическая консультация, но
выкладывать такое ядро публично рискованно.

Варианты:

| Вариант | Что получают пользователи MediaTek | Статус |
|---|---|---|
| **A. Релиз без ядра** (`.\build.ps1 -NoKernel`) | Ядро собирают сами: `kernel/build-kernel.sh` в любом Alpine-дистрибутиве WSL (≈30 мин), затем `airpodsctl kernel-install --from <папка>` | работает сейчас |
| B. Ядро без встроенной прошивки + загрузка прошивки из места, которое видит ядро WSL | Готовое ядро в релизе | не исследовано, не проверено |
| C. Мастер сам собирает ядро на ПК пользователя | Без ручных шагов, но ≈30 мин и ~2 ГБ на сборку | не реализовано |

Пока не выбран B или C, публикуйте вариант A и говорите об этом в описании релиза.
Если ядро всё же попадает в сборку (без `-NoKernel`), `build.ps1` кладёт рядом с ним `BUILDINFO` (тег и коммит исходников
Microsoft — требование GPL-2.0), патчи, скрипт сборки и `LICENCE.mediatek`.

## 3. Сборка

```powershell
.\build.ps1 -NoKernel
```

Результат — `dist\AirPodsControl-<версия>-win-x64-portable.zip` (≈110 МБ без ядра — два self-contained exe; с ядром ≈170 МБ). Версия задаётся в
`Directory.Build.props`. Exe не подписан: SmartScreen будет предупреждать, пока у проекта нет подписи кода.

## 4. GitHub

```powershell
git add -A
git commit -m "Driverless mode (WSL2 bridge), press-and-hold modes, bilingual README"
git remote add origin https://github.com/<user>/<repo>.git
git push -u origin main
```

Файлы больше 100 МБ GitHub в репозиторий не принимает: ядро (`modules.vhdx` ≈252 МБ) и архивы — только в Releases
(лимит 2 ГБ на файл). `.gitignore` уже исключает `dist/`, `bin/`, `obj/` и `assets/wsl-kernel/`.

Релиз: *Releases → Draft a new release*, тег `v<версия>`, приложить zip. Шаблон описания:

```
AirPods Control v0.1.0 — early release, tested on one PC (Windows 11, MediaTek MT7922, AirPods Pro 3).

- Driverless mode: listening modes and exact battery through a WSL2 bridge (Bluetooth pauses ~12–15 s per command).
- Press-and-hold modes on the stems (incl. Off) written to the AirPods.
- Requires WSL2 and a USB Bluetooth adapter. MediaTek adapters need the AirPods Control WSL kernel, which is NOT included in
  this release (firmware licence) — build it with kernel/build-kernel.sh. Intel/Realtek/Broadcom: untested.
- No Windows Test Mode, no Secure Boot change, no driver.
- Known issue: the WSL VM uses about 900 MB of RAM while the bridge is active (fix planned; `wsl --shutdown` frees it).
- The exe is not code-signed (SmartScreen warning).

Ранняя версия, проверена на одном ПК. Требуются WSL2 и USB-адаптер Bluetooth. Для MediaTek нужно ядро AirPods Control —
в этот релиз оно не входит (лицензия прошивки), его собирает kernel/build-kernel.sh. Без тестового режима Windows и без
отключения Secure Boot. Известная проблема: пока мост активен, ВМ WSL занимает около 900 МБ ОЗУ — будет исправлено в обновлениях.
```

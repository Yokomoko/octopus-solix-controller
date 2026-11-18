# Setting it up

How to get Solix Load Follower running, try it safely, tune it and leave it on a Raspberry Pi. For what it does, see the [README](../README.md).

## What you need

- An Anker SOLIX Solarbank 4 E5000 Pro on your home Wi-Fi.
- An Octopus Energy account with a **Home Mini** paired to your smart meter.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows, macOS or Linux. To run it on a Raspberry Pi you only need the build output (see below).

## Getting the details

You'll need four things.

1. **Octopus API key.** Log in to your Octopus account and go to [Personal details, then API access](https://octopus.energy/dashboard/new/accounts/personal-details/api-access) (if that link has moved, search "API access" in your account). It starts `sk_live_`. Treat it like a password. It gives full access to your account, not read-only.
2. **Octopus account number.** It's on your bills and at the top of your account page, in the form `A-1234ABCD`.
3. **Battery IP address.** Find the battery in your router's list of connected devices, and give it a fixed IP while you're there (often called a DHCP reservation). Otherwise the address can change and the app will lose it.
4. **Modbus turned on.** In the Anker app go to the battery, then the gear icon, then Third-Party Control Settings, and turn on Modbus TCP. It uses port 502 and unit ID 1.

The Home Mini and, if you're on Intelligent Octopus Go, the car are both found automatically from your account.

## Running it

```
git clone <this repo>
cd SolixLoadFollower/src/Solix.Worker

dotnet user-secrets set Octopus:ApiKey sk_live_xxxxxxxx
dotnet user-secrets set Octopus:AccountNumber A-1234ABCD
dotnet user-secrets set Battery:Host 192.168.1.50

dotnet run
```

Then open http://localhost:5080.

`dotnet user-secrets` keeps the key in your user profile, outside the repo, so it can't end up in git.

## Trying it out

1. Leave it in **Observe** for a few hours. It reads everything and shows what it *would* do, but writes nothing. Check the battery output and SOC match the Anker app, and that the "Export seen as negative" check turns to yes when you're exporting. If your meter doesn't report export, solar charging won't work.
2. Switch to **Live** with the cap at 300 W. The battery goes into third-party mode and the app schedule stops while it's live.
3. To stop, press **Ctrl+C** in the console or switch back to Observe. Either one writes 0 W and puts the battery back in the app mode it was in (Custom, for me). It also hands back when Windows starts shutting down or restarting (forced updates included) and when the laptop sleeps. Closing the window with the X might not give it time, but the next start sorts it out.

Only run one copy at a time. The battery only takes one Modbus client, and even Observe mode reads from it.

While it's Live it asks Windows not to drop into standby when idle, so the screen can still switch off without the laptop losing its network overnight. Closing the lid or pressing the power button still sleeps it.

Whatever you set on the dashboard (mode and caps) is saved to `data/dashboard-settings.json`, so if you leave it in Live it comes back up in Live after a restart or a reboot. Until you've changed something on the dashboard, it starts from `appsettings.json`, which defaults to Observe. Delete the file to go back to the config values.

## Settings

Everything lives in `src/Solix.Worker/appsettings.json` under `Controller`. The ones you're most likely to change:

| Setting | Default | What it does |
|---|---|---|
| `MaxW` | 300 | Discharge cap |
| `HardMaxW` | 800 | Highest cap the dashboard allows. Mine tops out at 810 W on a plug-in unit, so I set it to that |
| `ImportBufferW` | 15 | Import it aims to leave while discharging |
| `PollSeconds` | 45 | How often it reads the Home Mini when things are steady. Octopus allows 125 reads an hour |
| `FastPollSeconds` | 25 | Quickest read, after a big change (300 W of house load or 200 W of setpoint). Smaller changes from 150 W get a read somewhere between this and `PollSeconds`, using spare budget |
| `MeterCallsPerHour` | 120 | Most Home Mini reads in any hour, counting the usage refresh too. Octopus allows 125. The count is kept in the database, so a restart doesn't reset it |
| `SocFloorPercent` | 5 | Stops discharging at this SOC. Covering the house is the only thing allowed below the 10% planning floor |
| `BatteryCapacityWh` | 5000 | Usable capacity, used to turn SOC into energy |
| `Spare:ExportTargetW` | 5 | When there's clearly more charge than the evening needs, it aims for this tiny export instead of `ImportBufferW` |
| `Spare:MarginWh` | 200 | Kept back on top of the busiest evening in the last `Spare:HistoryDays` (7) days before it counts as plenty |
| `Spare:FloorPercent` | 10 | The planning floor: forecasts and "plenty" treat this as empty |
| `CheapRate:Start` / `End` | 23:30 / 05:30 | When to hand back to the app for cheap-rate charging. Keep it the same as your app schedule |
| `SolarCharge:StartExportW` | 50 | Export level that starts solar charging |
| `SolarCharge:TargetExportW` | 5 | Export it leaves while charging from solar |
| `SolarCharge:MinChargeW` | 75 | The battery ignores charge requests below this, so smaller ones get rounded up |
| `SolarCharge:MaxChargeW` | 800 | Charge cap for solar, EV smart-charge slots and Power Ups (up to 2300 W) |
| `EvSlot:ChargeW` | unset | Set this only if you want EV slots to charge at a different rate from the charge cap |
| `EvSlot:CarChargingW` | 6000 | Demand that counts as the car charging. Set it above a busy kitchen (oven and hobs can reach 5 kW), below what your car draws |
| `EvSlot:CarStillChargingW` | 1500 | Lower bar once the car has been seen in a slot, so tapering near full still counts |
| `EvSlot:HoldUnconfirmedCar` | true | Holds the battery at 0 W while the car charges outside a slot Octopus has confirmed, instead of draining it into the car |
| `EveningExport:Enabled` | false | Exports what's left in the battery before the cheap-rate window. Only turn it on with an export tariff that pays more than your overnight rate |
| `EveningExport:Start` | 21:00 | When exporting can start. It spreads the spare out so the battery reaches the floor just as the cheap rate starts |
| `EveningExport:FloorPercent` | 10 | Exporting stops here, leaving the rest (down to `SocFloorPercent`) for the house |

The mode and caps can also be changed from the dashboard, and those changes take priority over the file.

## Running it on a Raspberry Pi

A Pi Zero 2 W is plenty and costs about £1 a year to run. It has to be the **Zero 2 W**: the original Zero can't run .NET.

1. Flash **Raspberry Pi OS Lite (64-bit)** with [Raspberry Pi Imager](https://www.raspberrypi.com/software/), setting Wi-Fi, a username and SSH in its settings.
2. Build it on your PC:
   ```
   dotnet publish src/Solix.Worker -c Release -r linux-arm64 --self-contained -o publish
   ```
3. Copy `publish/` to the Pi (along with `data/samples.db` if you want your history), for example to `/opt/solix`. A publish overwrites `appsettings.json`, so re-apply any changes you've made to it on the Pi by editing the values in place.
4. Put the secrets in an environment file only root can read, say `/etc/solix.env` (`chmod 600`), using double underscores:
   ```
   Octopus__ApiKey=sk_live_...
   Octopus__AccountNumber=A-1234ABCD
   Battery__Host=192.168.1.50
   ```
5. Add `/etc/systemd/system/solix.service`:
   ```
   [Unit]
   Description=Solix Load Follower
   After=network-online.target
   Wants=network-online.target

   [Service]
   Type=notify
   WorkingDirectory=/opt/solix
   ExecStart=/opt/solix/Solix.Worker
   EnvironmentFile=/etc/solix.env
   Restart=always
   RestartSec=10
   WatchdogSec=120
   User=pi

   [Install]
   WantedBy=multi-user.target
   ```
   Change `User=pi` to the username you set in Imager, then `sudo systemctl enable --now solix`. `Restart=always` brings it back if it crashes, and `WatchdogSec` restarts it if it hangs: the control loop checks in with systemd every few seconds, so two minutes of silence means it's stuck.

6. To get a **Hand back and shut down** button on the dashboard, let the service user power the Pi off (change `pi` to your username):
   ```
   echo 'pi ALL=(root) NOPASSWD: /usr/bin/systemctl poweroff' | sudo tee /etc/sudoers.d/solix-power
   sudo chmod 440 /etc/sudoers.d/solix-power
   ```
   Pulling the plug doesn't give the app a chance to hand back, so the battery stays in third-party mode at its last setpoint, and the app schedule (including overnight charging) won't run until the Pi is back. Use the button, wait for the green light to stop flickering, then switch it off.

The battery has no watchdog of its own, so this matters. If the app dies without handing back, the battery carries on at its last setpoint until something tells it otherwise.

On the Pi the dashboard listens on your network, so it's at `http://<pi-ip>:5080` from your phone. It has no login, so keep it on your home network and don't port-forward it.

## How it's built

- `Solix.Core`: the control logic as pure functions, with tests. No I/O.
- `Solix.Worker`: an ASP.NET Core host. It contains the control loop (`ControlWorker`), the Octopus GraphQL client, the Modbus client (FluentModbus), SQLite logging, and the dashboard (`wwwroot/index.html`).
- `Solix.Tests`: MSTest.

The battery registers come from Anker's official Home Assistant integration. Mode is `10064` (3 for third-party control) and the setpoint is `10071` (+ to discharge, − to charge). The battery holds its last setpoint indefinitely, which is why the app hands back to the Anker app whenever it loses data or stops.

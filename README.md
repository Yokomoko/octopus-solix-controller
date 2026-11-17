# Solix Load Follower

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-green)](LICENSE)
[![Runs on Raspberry Pi](https://img.shields.io/badge/runs%20on-Raspberry%20Pi-C51A4A)](docs/SETUP.md#running-it-on-a-raspberry-pi)

**Get your plug-in Anker SOLIX battery following the house the way it would if it were hardwired.**

Got an Anker SOLIX Solarbank 4 E5000 Pro that isn't hardwired yet? Then you'll know Anker's own meter and Smart Mode are off the table, and the battery just runs a fixed schedule. Set the schedule too high and your cheap overnight charge goes out to the grid for nothing. Set it too low and you're paying the peak rate while a full battery sits there.

I didn't fancy either, so I built this. It reads my **Octopus Energy Home Mini** every 40 seconds and sets the battery's output to match what the house is drawing, all over the local network. It's been running my house from a Raspberry Pi Zero 2 W.

[How to set it up](docs/SETUP.md)

> **Disclaimer.** This is a personal project, not affiliated with or endorsed by Anker or Octopus Energy. It writes to your battery over Modbus and uses unofficial parts of the Octopus API that could change at any time. Use it at your own risk. Follow Anker's installation guidance for your battery; in the UK, Anker says the E5000 Pro must be hardwired by a qualified electrician.

## What it does all day

It watches what you're pulling from the grid and matches it, aiming to leave a trickle of import so it never pushes battery out to the grid. Put the kettle on and the output goes up to its cap, then comes back down when it boils.

It's also picked up a few tricks:

- An oven or hob thermostat clicking on and off every few minutes is quicker than the meter reports. If the battery chased it, you'd import while it's on and give charge away while it's off. It spots the pattern and covers the load underneath.
- If you've got plug-in solar on its own inverter, any power you'd export goes into the battery.
- It looks at the last week to work out what the battery usually supplies between now and the cheap rate, and where you'll probably finish tonight. On evenings with clearly more than enough, it uses the spare more freely.

## Intelligent Octopus Go

When Octopus gives your car a cheap slot, the battery charges at the cheap rate too. If the car starts charging before Octopus has confirmed the slot, it holds the battery at zero until it does, so you're never draining the battery into the car.

From 23:30 to 05:30 it hands control back to the Anker app, and your app schedule fills the battery at the cheap rate.

It charges during Power Ups and Happy Hours you've joined, and during Power Downs it covers as much import as it can. If you've got an export tariff, you can also have it send out whatever the house won't need before the cheap rate starts. On a busy evening it sends nothing.

## The dashboard

It's a web page you can open on your phone. It shows:

- what the grid, battery and house are doing right now, and why
- today's cost so far, split into car, house and standing charge
- a chart of the battery today against a usual day, with where you'll probably finish tonight and the worst case from the busiest recent evening (tap it for the figures at any time)
- an Observe/Live switch, the discharge and charge caps, and a button that hands the battery back and shuts the Pi down safely

## Safety

It starts in Observe mode, where it reads everything and shows what it *would* do without touching the battery. Nothing gets written until you switch it to Live.

If the Octopus data goes stale or gets rate-limited, or the app stops, it hands the battery back to the Anker app. The bottom 10% is kept as a planning floor, and it only goes below that, down to 5%, to cover the house. On the Pi, systemd restarts it if it crashes or hangs.

## What you need

- An Anker SOLIX Solarbank 4 E5000 Pro on your Wi-Fi, with Modbus TCP turned on in the Anker app.
- An Octopus Energy account with a Home Mini.
- Something to run it on. A Raspberry Pi Zero 2 W uses about £1 a year in electricity, or any Windows, Mac or Linux machine with .NET 10 will do.

Installation, settings and the Pi setup are all in [docs/SETUP.md](docs/SETUP.md).

It uses the Home Mini you've already got, so there's no extra meter to buy and your battery can be following the house today. If you get it hardwired with Anker's meter later on, Smart Mode can take over and you just switch this off.

## Licence

MIT. See [LICENSE](LICENSE).

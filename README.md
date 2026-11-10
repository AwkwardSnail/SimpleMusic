# Simple Music Melody

Write your notes first, count the bars later. A music editor for beginners:
place notes on the staff without worrying about bar lines, then let Simple Music Melody
split them into bars for you. Play it back, and export it as a PDF.

It is called SimpleMusic in the code and in this repository; its sister app is
[Simple Music Rhythm](https://github.com/AwkwardSnail/SimpleMusicRhythm).

Made by [aWkward sn@ail](https://awkwardsnail.com.au): calm, simple tools that
stay the way you left them.

## Get it

- **In your browser:** [fdimer.github.io/music-editor](https://fdimer.github.io/music-editor/)
- **Windows:** coming soon to the Microsoft Store.
- **Build it yourself (free):** see below.

## Running

The browser version is `index.html`, `app.js` and `styles.css`. Serve the folder
and open it:

```
python -m http.server 8173
```

The Windows version is a .NET 10 WPF app in `desktop/SimpleMusic`:

```
dotnet run --project desktop/SimpleMusic
```

`desktop/SimpleMusic/Packaging/build-msix.ps1` builds the MSIX package for the
Microsoft Store.

## Privacy

Simple Music Melody collects no data and never contacts an Awkward Snail server. Your
sheets stay on your device. See the
[privacy policy](https://awkwardsnail.com.au/privacy.html).

## Licence

The code is under the [MIT licence](LICENSE), so you're welcome to fork it. The
Awkward Snail name and icon are not covered by the licence: please don't use them
for your fork.

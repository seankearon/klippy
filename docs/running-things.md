---
icon: lucide/play
description: "Links, documents, scripts, applications, macros and file locations — and what Klippy will never do on its own."
---

# Running things

Some snippets are not text you want to paste — they are a link or a document you want
open, a script you want run, or an application you want started. A snippet can be marked
**Execute** in the editor (WHEN TRIGGERED → Execute), exactly as it can be marked
Markdown, and then triggering it — `Enter`, a click, a tap — runs it instead of copying
it. Without the marker nothing runs: Klippy never decides on its own that a snippet looks
like a link and should therefore be launched.

Marked rows show a small amber `run` marker, the way Markdown ones show `md`, and their
primary hover action becomes **▷** rather than the copy glyph. `Ctrl/⌘+Enter` always
copies, marker or not, so a snippet you usually run can still be put on the clipboard
when you want the text — the row's badge says both: `↵ run · Ctrl+↵ copy`.

What a marked snippet can run is a short allow-list, checked against its first word,
once any environment variable in it has resolved:

| Snippet starts with | Runs as |
|---|---|
| `http://`, `https://`, `mailto:`, or a bare `www.` | Opened in the default browser |
| `ms-settings:` — **Windows only** — or `x-apple.systempreferences:` — **macOS only** | That page of Settings or System Settings — see [Links](#links) |
| `edge://`, `chrome://` | That page of Edge or Chrome, in Edge or Chrome — see [Links](#links) |
| A kind of link you have added under **ALSO OPEN** — `vscode:`, `obsidian:` | Handed to whatever application registered that kind, as clicking it in a browser would — see [Other kinds of link](#other-kinds-of-link) |
| A `file:` link | Exactly what the path it spells would do — see [`file:` links](#file-links) |
| The full path of a document | Opened with whatever opens its kind, as double-clicking it would — see [Documents](#documents) |
| `*.ps1` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File` on Windows, `pwsh -NoProfile -File` elsewhere |
| `*.sh` | The script itself where it is executable, so its `#!` line chooses; otherwise `/bin/sh`. `bash.exe` (Git Bash, WSL) on Windows |
| `*.bat`, `*.cmd` | `cmd.exe /c` — **Windows only** |
| `*.exe` | Started directly, arguments and all — **Windows only** |
| `*.app` | `open -a`, which knows which executable inside the bundle to start — **macOS only** |
| `*.app/Contents/MacOS/<program>` | Started directly, so its arguments reach it as they would from a terminal — the command line JetBrains IDEs and Zed document — **macOS only** |
| `*.AppImage` | The image itself, which runs itself — **Linux only** |
| The full path of anything else — a folder, a log file, an `.hta` | Shown in Explorer or Finder: a folder opens, a file is selected in its folder — see [Showing where a file is](#showing-where-a-file-is) |
| anything else | Nothing — and the editor says so as you tick the marker, rather than leaving you to find out by pressing Enter |

The `.exe`, `.app` and `.AppImage` rows are what each platform calls an application, and
each runs only at home: an `.exe` is no more startable on a Mac than a `.bat` is, and
marking one there warns you in the editor rather than failing at the press of Enter. A
settings page has a home in the same way.

Everything after the first word is passed to the script or application as arguments,
quotes grouping the words that belong together: `deploy.ps1 --env "west europe"` passes
two arguments, not three. That is also how a path with a space in it stays one path —

```
"C:\Program Files\Klippy\Klippy.Desktop.exe" --minimised
```

— and without the quotes the first word is `C:\Program`, which names nothing. The editor
shows you that as you tick the marker: *Shows C:\Program in Explorer when triggered* is
the cue that a path has stopped at its first space.

The quotes are only needed where there are arguments to keep apart. A line that is nothing
but a path — a folder, a file, or a program with nothing after it — is also tried whole,
spaces and all, whenever its first word names nothing Klippy runs, and if the disk has
something there, that is what the line means. So `%protondrive%\Shine Forms\Graphics`
opens that folder, and `C:\Program Files\Klippy\Klippy.Desktop.exe` on its own starts
Klippy, quotes or no quotes; add `--minimised` and the line is no longer a path, so the
quotes are back to being what keeps the program in one piece. A variable
whose value contains a space needs no quotes at all, because it resolves *after* the line
has been split into words: `%LOCALAPPDATA%\Programs\WebStorm\bin\webstorm64.exe` is one
path however many spaces your user name has in it. Scripts and applications alike run from
their own folder, which is where each normally expects to be, and a bare `notepad.exe` is
left to Windows to find on `PATH`, as Run would.

## Links

A web link is not the only kind worth keeping. A page of Windows' Settings, the privacy page
of your browser, a note in Obsidian: each has a link of its own, and a snippet marked
Execute opens it.

```
ms-settings:display
ms-settings:windowsupdate
edge://settings/privacy
chrome://flags
x-apple.systempreferences:com.apple.preference.security?Privacy_Camera
```

Two kinds open without anything being added, because each only ever shows you a page of
something already on your machine:

- **Settings pages.** `ms-settings:` opens that page of Settings on Windows, and
  `x-apple.systempreferences:` that pane of System Settings on macOS. Each belongs to one
  platform, as an `.exe` does, and marking one on the other warns you in the editor.
- **Browser pages.** `edge://` and `chrome://` are pages of the browser itself, not of the
  web. No browser tells the operating system about them, so Windows has nothing to open
  `edge://settings` with. Klippy hands the page to the browser it belongs to instead, the way
  a desktop shortcut to one is written: `msedge.exe` or `chrome.exe` on Windows, found
  wherever the browser registered itself, as Run finds it; `open -a "Microsoft Edge"` or
  `"Google Chrome"` on macOS; `microsoft-edge` or `google-chrome` on Linux.

As with a web link, `%P%` in one is percent-encoded into it, and anything after the link on
the line is left behind. **The macOS and Linux browser commands have not been run**, for the
same reason as the macOS hotkey: they cannot be tested from Windows.

### Other kinds of link

`vscode://file/…` opens a file at a line in VS Code, `obsidian://open?…` a note, and
`zoommtg:`, `msteams:` and `slack:` a meeting or a channel. These open once you have added
their kind under **ALSO OPEN** in [Settings](settings.md): type `vscode:` (the colon is what
says it is a kind of link rather than of file) and press `Enter` or **Add**. Until then, the
editor says so as you tick the marker, and names what to add.

A kind on the list is handed to whatever application registered it, as clicking the link in a
browser would be. That is also why each kind is added on purpose rather than all of them
being open by default: what an application does with a link is up to the application, and
some do a great deal with one. The list takes a kind at a time, and it will not take these:

| Written | Why not |
|---|---|
| `file:` | A `file:` link is a path, and has [rules of its own](#file-links) |
| `javascript:`, `vbscript:`, `data:` | Each carries something to run rather than somewhere to go |
| `http:`, `https:`, `mailto:`, and the settings and browser pages above | They open already |

A link handed to an application has the characters no link carries percent-encoded on the
way: spaces, control characters, `"`, `<`, `>`, `` ` `` and `\`. That is what a browser does
before handing a link to an application, and for the same reason: an application registers
a command line with the link in it as `"%1"`, so a quote in a link off the clipboard could
end that argument and start one of its own. Encoded, it arrives as the one argument it is,
and an application that reads links decodes it anyway.

### `file:` links

A `file:` link is how an address bar, an email or a wiki names somewhere on disk, and to
Klippy it is that place in every respect. The path it spells is read exactly as if you had
written it: a folder opens, a [document](#documents) opens, a script runs with the rest of the
line as its arguments, and a log file is [shown where it is](#showing-where-a-file-is).

```
file:///C:/Users/sam/Documents/
file:///C:/My%20Docs/release-notes.html
file:///D:/tools/deploy.ps1 --env west
file://nas/media/Films
```

The link is decoded before anything looks at it, so an encoded `%2E` hides no extension, and
one that decodes to a quote or a control character is refused. It never reaches the shell as
a link, only as the path. A program named by one starts as its path would, on its own platform
and with its arguments judged, so the link is a way of spelling a path, not a way round a rule.

On Windows a link may also name a share, as Windows' own links do. `file://nas/media/Films` is
`\\nas\media\Films`, and so are `file:////nas/…` and `file://///nas/…`, which some programs
write. `file://C:/Docs/…`, with the drive where the host would go, is read as the drive. On
macOS and Linux a share has no path until it is mounted, so a `file:` link naming another
machine is refused.

## Documents

A web page, a PDF or a picture kept on disk is not something to run, but it is something
to open — the same gesture as double-clicking it, handed to whatever your machine opens
that kind of file with. Name it by its full path, or paste the `file:///` link your
browser's address bar shows for it:

```
C:\Docs\release-notes.html
"C:\My Docs\release-notes.html"
%USERPROFILE%\Docs\release-notes.html
file:///C:/My%20Docs/release-notes.html
```

A document has to say where it is — a bare `release-notes.html` would be looked for
beside Klippy itself, which is nobody's idea of where their notes are. It takes no
arguments, so anything after it on the line is left behind, as it is for a link.

Only these kinds open:

| Kind | Extensions |
|---|---|
| Web pages | `.html`, `.htm` |
| Reading | `.pdf`, `.txt`, `.md` |
| Pictures | `.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.svg` |
| Office, without macros | `.docx`, `.xlsx`, `.pptx`, `.odt`, `.ods`, `.odp` |

It is a short list on purpose. To the shell, *open* and *run* are the same verb and the
extension decides between them: opening a `.js`, `.vbs`, `.hta`, `.lnk` or `.scr` on
Windows, or a `.command` on macOS, runs it. These are the kinds that are read.

A `file:` link to one of these opens it too, being the same file as the path it spells, and
a link to any other kind of file is shown, as that path would be. See
[`file:` links](#file-links).

### Opening other kinds of file

A solution you want open in your IDE is not on that list, and should not be by default:
opening a `.sln` or `.slnx` in Visual Studio or Rider runs the build's own targets as it
loads, which is closer to running something than to reading it. But it is *your* solution,
and whether it opens with a keystroke is your call to make. So the list can be added to,
under **ALSO OPEN** in [Settings](settings.md): type `.slnx` and press `Enter` or **Add**,
and click a kind's chip to take it back off. Each change is saved as it is made. The same
list by hand, in `settings.json`:

```json
{
  "ExecuteOpenExtensions": [".slnx", ".sln"]
}
```

The same box takes [kinds of link](#other-kinds-of-link) too, told apart by their colon:
`.slnx` is a kind of file and `vscode:` a kind of link, and each chip is saved to the list it
belongs to — `ExecuteOpenSchemes` for the links.

A kind of file on it opens exactly as the documents above do — by its full path or a `file:///`
link, with whatever your machine opens that kind with — from an item marked Execute and from
a [variable typed by name](variables.md#typing-a-name). A path typed into the search box is
still only [shown](unmatched-search.md), whatever the list says. Write an extension as
`.slnx`, `slnx` or `*.slnx`; case is no matter.

It only ever adds. A script or an application is never opened this way, listed or not —
Settings will not take one, and says why:
each of those already runs, on its own platform and with its arguments judged, and handing
one to the shell instead would be a way round both — a `.bat` opened is a `.bat` run with
nobody having looked at its path. Everything else is yours to decide, which is also the
warning: put `.lnk` or `.js` on the list and opening one is starting what it points at, as
double-clicking it would be.

## Environment variables

The first word of a marked snippet may name itself the way a path does everywhere else on
the machine, in both dialects wherever you are: `%LOCALAPPDATA%` as on Windows, `$HOME` and
`${HOME}` as on macOS and Linux, plus a leading `~`. Windows will not do this for you —
only `cmd.exe` ever looked inside a file name, and nothing here goes through `cmd` — so
without it `%LOCALAPPDATA%\Programs\WebStorm\bin\webstorm64.exe` is a folder with percent
signs in its name and the launch fails on a path that plainly exists.

A name that does not resolve is left exactly as written, so it stays a string naming
nothing rather than quietly becoming a path with a hole in the middle of it. Two things
the machine's names deliberately do **not** reach:

- **Arguments.** Only the first word resolves *against the machine*. An argument keeps its
  percent signs, so the `.bat` refusal below still sees what `cmd.exe` would see, and a
  child process inherits the environment and can read its own `%APPDATA%` anyway.
  Klippy's own [defines](variables.md) are not in that boat — nothing downstream has ever
  heard of `variables.txt`, so an unresolved `%app%` would arrive as a path with percent
  signs in the middle of it, naming nothing. Those resolve in every word, exactly as
  copying the same line has always done. A whole argument *typed* after a quick-code is
  the third case, and [names a define](variables.md#variables-as-arguments) rather than
  containing one: the file's own names, never the machine's, so typing `path` gets you the
  word `path`.
- **A macro's value.** `%C%` holding a path with a `%VAR%` in it is left as it stands: a
  macro's value is data rather than more text to read, the same rule that keeps it from
  becoming a second command. It also means a clipboard holding `C:\100%discount%off\tool.exe`
  keeps its middle.

The same shorthands, read the same way, on the typed route — see
[Running an unmatched search](unmatched-search.md). Two routes to the same
launcher should not disagree about what a variable means.

Klippy's own [variables file](variables.md) sits in front of the machine here:
a name defined in `variables.txt` answers first, and the environment answers everything else.
So `%ws%` names WebStorm on this machine with no environment variable to set, and
`%LOCALAPPDATA%` keeps working exactly as above. It also reaches further than the machine
does — a define resolves in an argument as well as in the first word, so
`%ws% %src%\myapp` runs as the line it copies as.

## Macros

A snippet's text can carry placeholders, filled in when it is copied or run — the
marker decides which of those happens, the macros work either way:

| Macro | Expands to |
|---|---|
| `%P%` | A positional argument — what you typed after the quick-code |
| `%P:file%` | The same, saying which [flavour](variables.md#one-name-two-flavours) of a define it wants when the argument names one |
| `%P:exact%` | The same, saying the argument is never a name — the word reaches the item as typed |
| `%C%` | Whatever text is on the clipboard right now |

Environment variables are not macros and the asymmetry is real: a `%VAR%` in the first
word resolves when an item **runs**, and a copy leaves it alone. A macro is the item's;
a variable belongs to the path the item names.

Say a snippet holds `https://www.google.com/search?q=%P%` behind the quick-code `?`.
Typing

```
? stuff
```

shows `https://www.google.com/search?q=stuff` on the row — the expansion, before you
have committed to anything — and `Enter` runs it if the snippet is marked, or copies
exactly that text if it is not. The *last* `%P%` takes every argument still unused, so
`? cats and dogs` searches for the phrase rather than throwing two thirds of it away. A
placeholder with nothing to fill it expands to nothing: half a typed invocation never
leaves `%P%` on the clipboard.

An argument may *name* something rather than spell it out: where `variables.txt` defines
`app` as a solution file, `r app` passes that path, and `r "app"` passes the word `app`.
A name defined once per [flavour](variables.md#one-name-two-flavours) lets the item pick — `%P:file%`
against `%P:folder%` — so the same word means the right thing at either of them, and an
item whose arguments are never names says so once with `%P:exact%` instead of asking
whoever invokes it to quote them every time. See
[Variables as arguments](variables.md#variables-as-arguments).

A line is only read as an invocation when its first word is **exactly** somebody's
quick-code and something follows it. Otherwise it is the ordinary search it has always
been — a prefix match would hijack every two-word search whose first word happened to
start with a quick-code.

Running hands the execution engine the snippet as stored *and* the arguments —
`https://www.google.com/search?q=%P%` and `stuff` — rather than the finished string,
because only it knows where each value is about to land: a URL's query string gets
`cats%20and%20dogs`, a script's argument list gets `cats and dogs`. `%C%` is read once,
at that moment, and only when the snippet actually carries one: a row previews its own
`%P%` expansion as you type, but Klippy never reads your clipboard to draw a list.

A `%C%` can carry the whole command — a snippet of just `%C%`, marked Execute, runs
whatever is on the clipboard, link or script or application path and switches alike,
which is the other half of the clipboard history.

## What running something will not do

Running a snippet is running code, so the edges are drawn deliberately tightly:

- **Nothing runs unmarked.** The marker is stored on the snippet and defaults to off, so
  every snippet that exists today — and every one an import brings in — goes on being
  copied.
- **Only the allow-list above runs**, applied to the first word once its variables have
  resolved. A snippet naming something that is neither link, document, script nor
  application is not runnable however firmly it is marked, so `docker system prune -af`
  stays text. A full path to anything else is [shown](#showing-where-a-file-is) rather
  than run, which starts nothing: an `.hta` is pointed at in Explorer, never opened. A
  scheme is not a path whatever it ends in, either: `ms-msdt:/id x\cmd.exe` names an
  `.exe` without being one, and is refused along with `javascript:` and every other kind
  of link nobody added. A `file:` link is the one scheme that *is* a path, and it gets
  exactly [what that path would](#file-links) — no more, since the link never reaches the
  shell. The one way to widen what opens is to
  [name a kind yourself](#opening-other-kinds-of-file), and even that never reaches a
  script or an application.
- **An application is a program, and starting one is starting a program.** That is the
  point of the feature and also its edge: a snippet of `%C%` marked Execute will start
  whatever application path is on the clipboard, `cmd.exe` and its switches included. The
  marker is the gate — it is stored per snippet, defaults to off, and you put it there.
- **Arguments are passed as arguments**, never as a command line a shell re-reads. A
  `%C%` holding `; rm -rf ~` is one argument to the script, and stays one.
- **Except for `.bat`**, which `cmd.exe` re-parses after .NET has quoted it. An argument
  carrying `& | < > ^ " %` is refused with a message instead, because pretending to
  escape it would be a lie. The refusal is judged on the argument *as typed*, before
  anything resolves — a variable defined as `a&b` would otherwise smuggle an ampersand
  past the one check that exists to catch it — so `%APPDATA%` as an argument to a `.bat`
  is refused too. **The `.bat` file's own path faces the same rule**, minus the `%`: it
  rides the same line cmd.exe re-reads, and .NET quotes only what carries a space, so a
  folder genuinely called `R&D` would start a second command.
- **A path carrying a double quote is refused** — anywhere but wrapping the whole of it.
  Windows stops the program name at the quote while the allow-list reads the extension off
  the end, so `payload.scr"x.exe` would pass as an `.exe` and start the `.scr`. A matched
  pair around the whole word is the one safe case and comes off first, since that is how a
  *Copy as path* paste and a variable written for a shell both arrive; a Windows path
  cannot contain a quote at all, which is what makes both rules sound.
- `-File` rather than `-Command` for PowerShell, for the same reason: the arguments stay
  arguments instead of being parsed as more PowerShell. `-ExecutionPolicy Bypass` goes
  with it, since the script is one you keep in Klippy and have just asked for by name.

Running something dismisses the window, as copying can: the browser, the script or the
application is where you are going next. On a phone a marked link is handed to the phone to
open — a web link in the mobile browser — while a marked document, script, application or
path says there is nothing to open it with rather than doing nothing, and `%C%` and `%P%`
expand on a copy there as they do everywhere. A settings page belongs to a desktop, and
ALSO OPEN is a desktop setting, so neither reaches a phone.

## Showing where a file is

A path you keep in Klippy is often somewhere you want to *go* rather than something to
paste: a logs folder, the file a support call is about, the installer you downloaded. Klippy
resolves it — its variables, `~`, a pair of quotes, a `file:` link — and shows it in the
platform's file manager. A folder opens; a file is shown selected in the folder it is in.
Nothing is opened and nothing runs, so any kind of file can be shown, where only the short
list of [documents](#documents) can be opened.

There are three ways to get there:

- **`Ctrl/⌘+R`, or the folder button in a row's hover actions** on desktop, on any row
  whose text names a path — a snippet, marked or not, or a clip in the
  [clipboard history](clipboard-history.md). A clip of copied files shows the first of them.
  The button only appears where the row's text reads as a full path; whether the file is
  still there is checked when you press it, and the toast says *Not found* if it is not.
- **A snippet marked Execute** whose first word is the full path of anything that is not a
  link, document, script or application, or a `file:` link to one — the second-to-last row
  of the table above.
  `%LOCALAPPDATA%\Klippy` or `D:\work\invoices` opens the folder, and `C:\logs\app.log` is
  shown selected in `C:\logs`. A path with a space in it needs no quotes when it is the
  whole line; otherwise the rest of the line is left behind, as it is for a document.
- **Typing the path** into the search box when nothing else matches — see
  [Running an unmatched search](unmatched-search.md).

For the first of these, a snippet is read as a copy would put it on the clipboard, its
variables and macros filled in, and then as a line typed into the search box: the whole of
it is the path, spaces and all, since a path copied out of an address bar has no quotes round
them. If nothing is there, its first word is tried the way a run reads it, so a snippet of
`%ws% %src%\myapp` shows WebStorm itself.

| Platform | What it runs |
|---|---|
| Windows | `explorer.exe` with the folder, or `explorer.exe /select, <file>` |
| macOS | `open <folder>`, or `open -R <file>` — Finder's *Show in Enclosing Folder* |
| Linux | `xdg-open` with the folder — for a file, the folder it is in, since there is no one file manager to ask for a selection in |

Two edges, both about not letting "show" turn into "run":

- **A macOS package is shown, not opened.** A `.app`, a `.prefPane` or an installer bundle
  is a folder to the disk, but `open` starts it or installs it. So on a Mac a folder whose
  name has an extension is shown selected in its parent rather than opened — an ordinary
  folder called `my.project` costs you one double-click.
- **Explorer reads its own command line**, and a comma ends a path there. A path with a
  comma in it and no space would reach Explorer unquoted and could carry a switch of its
  own, so it is refused with a message; one that has a space, like `OneDrive - Acme, Inc`,
  is quoted on the way and is fine. On Windows a path also has to start with a drive or a
  `\\share` — a path starting with `/` is a Unix path, and Explorer would read it as a switch.

## Pull first

Scripts tend to live in a checkout, and a checkout goes stale. With **Pull first** on,
running a script or an application runs `git pull` in its own folder and waits for it
before starting anything — so what runs is what is in the repository, not what was on
disk the last time you thought about it. It applies to a marked item and to an
[unmatched search](unmatched-search.md) alike, since both end at the same
launcher.

It is a *separate process*, not a prefix: `git -C <folder> pull`, started the same way
everything else is, with its arguments as arguments. Klippy never builds a command line
for a shell to re-read — that is the property the whole execution path is built on, and
`git pull && …` would be the one place it was given up. It also means the pull can be
waited for and its exit code read, which a shell prefix could not offer.

- **Only a script or an application.** A link has no working copy, and a folder is being
  opened rather than run.
- **Only a real checkout.** The folder is walked up looking for `.git` — a directory in a
  clone, a file in a worktree or submodule — and the nearest one wins, so a script in a
  submodule pulls the submodule rather than its parent. Somewhere that is not a checkout
  is left alone, silently.
- **Only a rooted path.** A bare `notepad.exe` for Windows to find on `PATH` names no
  folder, and must not be read as one relative to wherever Klippy is running. A path named
  through a variable *is* rooted once it resolves, so a marked item behind `%LOCALAPPDATA%`
  is now eligible for a pull where it silently was not.
- **A failed pull does not cancel the run.** Off the network, on a conflicted branch, with
  no git installed: the script still starts and the toast says so —
  *Running deploy.ps1 — git pull failed (1)*. The pull is there to make what starts
  current, not to be a gate on starting at all. A pull that works says nothing, because
  it is what you asked for.
- **It waits, with a limit.** Thirty seconds, then the pull is killed and the run goes
  ahead with a note. `GIT_TERMINAL_PROMPT=0` goes with it, so a repository that wants a
  password fails in the moment instead of hanging on a prompt no one can see.
- The pull happens **before** the check that the target is there, so a script added in a
  commit this checkout has not seen yet is fetched rather than refused.

Off by default: it only makes sense where the things you run are kept in a checkout, it
costs a round trip to the remote on every run, and it is a network call made on your
behalf. In `settings.json` the key is `ExecutePullFirst`.

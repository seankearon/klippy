---
icon: lucide/terminal
description: "Type a line nothing matches and run it, with the same policy as a snippet."
---

# Running an unmatched search

A search that matches nothing is usually a typo. Sometimes it is an instruction. When the
list comes up empty and what you typed names something runnable, Klippy offers to run it —
in a band where the first row would have been, carrying the same `↵` badge the rows do.
`Enter` runs it, and so does a click.

It is the feature above reached from the other end. A marked snippet is text you decided
in advance was runnable; this is a line you have just typed. Either way what comes out is
an [`ExecutionPlan`](https://github.com/seankearon/klippy/blob/main/Klippy/Services/ExecutionPolicy.cs) handed to the same
[`ProcessLauncher`](https://github.com/seankearon/klippy/blob/main/Klippy/Services/ProcessLauncher.cs), so there is one place in Klippy
that starts anything, and one toast that says how it went.

**An item that matches beats the offer — where the line could have been a search for it.**
A snippet called "Lock the server room door" keeps `lock` a filter for as long as it
exists: `lock` is an ordinary word, and that snippet is a plausible answer to it.

A rooted path or a link is not an ordinary word. Nobody types `D:\work\tools\` hoping to
filter a list, so a snippet whose body merely *mentions* that folder — a path to something
inside it, say, which matches every word of it — does not take the folder away from you.
There the offer stands beside the matches, and `Enter` runs it.

Being the line still beats mentioning it: a snippet whose text **is** the link you typed is
what you were looking for, and keeps both the selection and the keystroke. So does one
whose text is what the line resolved to, which is how `%APPDATA%` and the folder it expands
to stay the same request.

When the offer does stand beside a list with rows in it, it takes the selection and looks
the part — the same accent bar, accent label and `↵` badge a selected row carries — and the
list shows a badge on nothing, because only one of the two can have the keystroke. `↓` moves
into the list, where `Enter` activates the row exactly as it always did, and `↑` from the
first row comes back to the offer. It is a position in the same column of things, not a
banner above them.

In the clipboard history none of that applies and a matching clip always wins: clips are
mostly paths and links themselves, so a line that looks like one is far more likely to be
someone hunting for the clip they copied than an instruction.

| What you type | What happens |
|---|---|
| `%appdata%`, `$HOME`, `~/work`, `%appdata%\Klippy` | The variable expands and the folder opens |
| `C:\work\invoices`, `\\nas\share`, `/usr/local/bin` | The folder opens in Explorer / Finder |
| `C:\tools\deploy.ps1`, `D:\apps\thing.exe`, `/Applications/Safari.app` | The script or application runs, exactly as a marked snippet naming it would |
| `http://…`, `https://…`, `www.…` | The page opens |
| `lock`, `sleep`, `hibernate`, `restart` | The machine control, after a confirmation |

Two things are deliberately narrower here than for a snippet you marked yourself, because
a marked item was written on purpose and this is whatever landed in a filter box:

- **Links are `http:`, `https:` and a bare `www.` only.** A marked snippet may also carry
  `mailto:`; typed text may not, and the rest of the schemes were never on the list. A
  typed `http://` opens on the scheme you typed — a dev server on `localhost:8000`, or a
  box on the LAN, answers on that and nothing else, so quietly promoting it to `https://`
  would send you to a port with nothing listening on it. A bare `www.` still gets an
  `https://` in front of it, exactly as a browser does.

  Writing the scheme out is you saying you meant a link, so the host is taken as you spelled
  it: `http://localhost:8000` and `http://build-server/job/klippy` open, dot or no dot. A
  bare `www.` has said no such thing, so it still wants a dot with something either side of
  it — otherwise a half-typed `www.` would count as a host.
- **Paths must be rooted** — a drive, a UNC share, a leading `/`, a `~`, or a variable that
  expands to one. A relative path would resolve against wherever Klippy happened to be
  started from, which is nobody's mental model, and without the rule every unmatched word
  with a dot in it would look like a file.

Everything else is the allow-list you already know: a script or an application Klippy can
run on this platform, and nothing besides. A folder is the one addition — it is opened,
not executed — so a typed `C:\work\notes.txt` is still just text that matched nothing.

**Quotes come off.** Explorer's Shift+right-click → *Copy as path* wraps what it gives you
in double quotes, whether or not the path has a space in it, so a pasted path would
otherwise be a string starting with a quote and match nothing at all. A line wrapped in a
pair of them is unwrapped before anything else looks at it — both quotes or neither, since
an unmatched one is a half-finished paste and a Windows path cannot contain a quote
anyway. A snippet marked Execute has always tolerated them, because its line goes through
the argument splitter; this is the same courtesy on the typed route.

Environment variables read here exactly as they do in a marked snippet — see
[Environment variables](running-things.md#environment-variables) — which is the point: `%APPDATA%` names one
folder whether you typed it into the filter box or wrote it into an item. A name that does not
resolve is left exactly as typed, so it stays a string that matches nothing rather than
quietly becoming a path with a hole in the middle of it. An item's own `%C%` and `%P%` are
left alone — those are filled when an item runs, and a search box is not an item.

The four machine controls are matched as the whole line and nothing else, so "restarting"
and "please restart" stay searches. Each asks before it happens, and that confirmation is
one `Enter` away so the whole gesture stays on the keyboard; it can be switched off. They
are planned as ordinary processes, which is why they need no second execution path:

| Control | Windows | macOS | Linux |
|---|---|---|---|
| **lock** | `rundll32 user32.dll,LockWorkStation` | `CGSession -suspend` | `loginctl lock-session` |
| **sleep** | `rundll32 powrprof.dll,SetSuspendState` | `pmset sleepnow` | `systemctl suspend` |
| **hibernate** | `shutdown /h` | not a thing on macOS — the word stays an ordinary search there | `systemctl hibernate` |
| **restart** | `shutdown /r /t 0` | `osascript … System Events restart` | `systemctl reboot` |

Windows carries the documented wrinkle that with hibernation enabled, asking for sleep
gets you hibernation: `SetSuspendState` is a request and the power policy decides. Turning
hibernation off behind your back is not Klippy's to do. **The macOS and Linux commands
compile but have not been run**, as with the macOS hotkey — they cannot be tested from
Windows.

Whether a typed line means anything is decided by
[`UnmatchedSearch`](https://github.com/seankearon/klippy/blob/main/Klippy/Services/UnmatchedSearch.cs), which is pure in the way
[`ExecutionPolicy`](https://github.com/seankearon/klippy/blob/main/Klippy/Services/ExecutionPolicy.cs) is: the platform is a parameter,
the file system is reached through an injected probe and the environment through another,
so the rules that decide whether
to execute typed text are the ones the tests pin down hardest. A run that fails reports in
the ordinary toast and leaves the window up to be read.

**Desktop only.** The offer is a keyboard gesture in a launcher, and a phone has neither a
shell to hand a path to nor a machine of its own to lock — the same reasoning that keeps
the command MRU off mobile.

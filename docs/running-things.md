---
icon: lucide/play
description: "Links, scripts, applications and macros — and what Klippy will never do on its own."
---

# Running things

Some snippets are not text you want to paste — they are a link you want open, a script
you want run, or an application you want started. A snippet can be marked **Execute** in
the editor (WHEN TRIGGERED → Execute), exactly as it can be marked Markdown, and then
triggering it — `Enter`, a click, a tap — runs it instead of copying it. Without the
marker nothing runs: Klippy never decides on its own that a snippet looks like a link
and should therefore be launched.

Marked rows show a small amber `run` marker, the way Markdown ones show `md`, and their
primary hover action becomes **▷** rather than the copy glyph. `Ctrl/⌘+Enter` always
copies, marker or not, so a snippet you usually run can still be put on the clipboard
when you want the text — the row's badge says both: `↵ run · Ctrl+↵ copy`.

What a marked snippet can run is a short allow-list, checked against its first word,
once any environment variable in it has resolved:

| Snippet starts with | Runs as |
|---|---|
| `http://`, `https://`, `mailto:`, or a bare `www.` | Opened in the default browser |
| `*.ps1` | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File` on Windows, `pwsh -NoProfile -File` elsewhere |
| `*.sh` | The script itself where it is executable, so its `#!` line chooses; otherwise `/bin/sh`. `bash.exe` (Git Bash, WSL) on Windows |
| `*.bat`, `*.cmd` | `cmd.exe /c` — **Windows only** |
| `*.exe` | Started directly, arguments and all — **Windows only** |
| `*.app` | `open -a`, which knows which executable inside the bundle to start — **macOS only** |
| `*.app/Contents/MacOS/<program>` | Started directly, so its arguments reach it as they would from a terminal — the command line JetBrains IDEs and Zed document — **macOS only** |
| `*.AppImage` | The image itself, which runs itself — **Linux only** |
| anything else | Nothing — and the editor says so as you tick the marker, rather than leaving you to find out by pressing Enter |

The last three are what each platform calls an application, and each runs only at home:
an `.exe` is no more startable on a Mac than a `.bat` is, and marking one there warns
you in the editor rather than failing at the press of Enter.

Everything after the first word is passed to the script or application as arguments,
quotes grouping the words that belong together: `deploy.ps1 --env "west europe"` passes
two arguments, not three. That is also how a path with a space in it stays one path —

```
"C:\Program Files\Klippy\Klippy.Desktop.exe" --minimised
```

— and without the quotes the first word is `C:\Program`, which names nothing. A variable
whose value contains a space needs no quotes at all, because it resolves *after* the line
has been split into words: `%LOCALAPPDATA%\Programs\WebStorm\bin\webstorm64.exe` is one
path however many spaces your user name has in it. Scripts and applications alike run from
their own folder, which is where each normally expects to be, and a bare `notepad.exe` is
left to Windows to find on `PATH`, as Run would.

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
  resolved. A snippet naming something that is neither link, script nor application is not
  runnable however firmly it is marked, so `docker system prune -af` stays text. A scheme
  is not a path whatever it ends in, either:
  `file:///C:/Windows/System32/cmd.exe` names an `.exe` without being one, and is
  refused along with `javascript:`.
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
application is where you are going next. On a phone a marked link opens in the mobile
browser; a marked script or application says there is nothing to run it in rather than
doing nothing, and `%C%` and `%P%` expand on a copy there as they do everywhere.

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

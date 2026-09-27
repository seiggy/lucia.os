# Lucia node-setup screen, drawn on the installer's spare console (tty5).
# Sourced by discover-and-wait and finish-install. It only writes output and
# never waits for a key: machines are expected to run with no keyboard.

SCREEN_TTY=/dev/tty5
E=$(printf '\033')
S= screen_ok=0 compact=0 rows=25 cols=80 x0=1 y0=1
s1=wait s2=wait s3=wait s4=wait s5=wait d1= d2= d3= d4= d5=
CODE= PANEL= NOTE1= NOTE2= FAIL_WHAT= FAIL_TODO= FAIL_DETAIL=
FAIL_NOTE='Safe to power off. No disks were changed.'
host=${LUCIA_SERVER:-}
host=${host#https://}
host=${host%%/*}
# Lucia dark palette: page, failed-bg, green-bg, amber-bg, accent, surface-subtle,
# surface, muted, line, failed-ink, green-ink, amber-ink, ocean, plum, dim, text.
PALETTE="$E]P0121722$E]P13b2826$E]P2203b30$E]P3332b1f$E]P4285bdd$E]P5242c3c$E]P61b2230$E]P7b1bbcd\
$E]P8343e50$E]P9f2b2a5$E]PA95d8b4$E]PBf2cc8c$E]PC97b3ff$E]PDc6a2ed$E]PE7d889c$E]PFeef2fa"

at() { S="$S$E[$((y0 + $1));$((x0 + $2))H"; }
fg() { if [ "$1" -ge 8 ]; then S="$S$E[1;3$(($1 - 8))m"; else S="$S$E[22;3$1m"; fi; }
bg() { S="$S$E[4$1m"; }
t() { S="$S$*"; }

screen_open()
{
    { : >"$SCREEN_TTY"; } 2>/dev/null || return 0
    screen_ok=1
    set -- $(stty size <"$SCREEN_TTY" 2>/dev/null)
    rows=${1:-25} cols=${2:-80}
    if [ "$cols" -lt 74 ] || [ "$rows" -lt 24 ]; then compact=1; fi
    x0=$(( cols > 72 ? (cols - 72) / 2 + 1 : 1 ))
    y0=$(( rows > 24 ? (rows - 24) / 2 + 1 : 1 ))
    # Keep kernel chatter off the screen; syslog on tty4 still has it.
    { echo 1 >/proc/sys/kernel/printk; } 2>/dev/null || true
}

screen_front() { [ "$screen_ok" = 1 ] && printf '%s' "$E[12;5]" >"$SCREEN_TTY" 2>/dev/null; :; }
screen_back() { [ "$screen_ok" = 1 ] && printf '%s' "$E[12;1]" >"$SCREEN_TTY" 2>/dev/null; :; }

hero()
{
    fg 11
    at 0 0; t ' ▄  ▀  ▄ '
    at 1 0; t '▄ ▄███▄ ▄'
    at 2 0; t '  ▀███▀  '
    at 3 0; t ' ▀  ▄  ▀ '
    fg 15
    at 0 13; t '██'; fg 11; at 0 37; t '▄▄'; fg 15
    at 1 13; t '██  ▄▄    ▄▄    ▄▄▄▄▄▄  ▄▄    ▄▄▄▄▄▄'
    at 2 13; t '██  ██    ██  ██        ██  ██    ██'
    at 3 13; t '██  ▀▀▄▄▄▄██  ▀▀▄▄▄▄▄▄  ██  ▀▀▄▄▄▄██'
    fg 7; at 1 54; t 'homelab'
    fg 14; at 2 54; t 'node setup'
}

step()
{
    at "$1" 0
    case $2 in
        ok) fg 10; t '[ ok ]'; fg 15 ;;
        run) fg 12; t '[ >> ]'; fg 15 ;;
        fail) fg 9; t '[FAIL]'; fg 15 ;;
        off) fg 14; t '[ -- ]' ;;
        *) fg 14; t '[    ]' ;;
    esac
    t "  $(printf '%.34s' "$3")"
    at "$1" 44
    case $2 in ok|off|wait) fg 14 ;; fail) fg 9 ;; *) fg 7 ;; esac
    t "$4"
}

glyph()
{
    case $1 in
        0) a='█▀█' b='█ █' c='▀▀▀' ;; 1) a='▄█ ' b=' █ ' c='▀▀▀' ;;
        2) a='▀▀█' b='█▀▀' c='▀▀▀' ;; 3) a='▀▀█' b=' ▀█' c='▀▀▀' ;;
        4) a='█ █' b='▀▀█' c='  ▀' ;; 5) a='█▀▀' b='▀▀█' c='▀▀▀' ;;
        6) a='█▀▀' b='█▀█' c='▀▀▀' ;; 7) a='▀▀█' b='  █' c='  ▀' ;;
        8) a='█▀█' b='█▀█' c='▀▀▀' ;; 9) a='█▀█' b='▀▀█' c='▀▀▀' ;;
        A) a='█▀█' b='█▀█' c='▀ ▀' ;; B) a='█▀▄' b='█▀▄' c='▀▀ ' ;;
        C) a='█▀▀' b='█  ' c='▀▀▀' ;; D) a='█▀▄' b='█ █' c='▀▀ ' ;;
        E) a='█▀▀' b='█▀ ' c='▀▀▀' ;; F) a='█▀▀' b='█▀ ' c='▀  ' ;;
        -) a='   ' b='▀▀▀' c='   ' ;; *) a='   ' b='   ' c='   ' ;;
    esac
}

fill() { at "$1" 0; bg "$2"; t "$(printf '%72s' '')"; }

# wrap ROW LINES COLOR TEXT [COLUMN]: word-wrap into at most LINES rows.
wrap()
{
    r=$1 n=$2 c=${5:-3}
    fg "$3"
    set -f; old=$IFS IFS='
'
    for line in $(printf '%s\n' "$4" | awk -v w=$((69 - c)) -v n="$n" '{
        for (i = 1; i <= NF; i++) {
            if (l != "" && length(l) + 1 + length($i) > w) { print l; l = ""; if (++k == n) exit }
            l = l == "" ? $i : l " " $i
        }
    } END { if (l != "" && k < n) print l }'); do
        at "$r" "$c"; t "$line"; r=$((r + 1))
    done
    IFS=$old; set +f
}

panel()
{
    if [ "$PANEL" = fail ]; then
        for r in 14 15 16 17 18 19; do fill "$r" 1; done
        at 15 3; fg 9; t "$(printf '%.66s' "Setup stopped: $FAIL_WHAT")"
        wrap 16 2 15 "$FAIL_TODO"
        at 18 3; fg 14; t "$FAIL_NOTE"
        bg 0
        [ -z "$FAIL_DETAIL" ] || wrap 21 2 7 "Reason: $FAIL_DETAIL" 0
    elif [ -n "$CODE" ]; then
        for r in 14 15 16 17 18 19; do fill "$r" 6; done
        at 14 3; fg 14; t 'VERIFICATION CODE'
        code=$(printf '%s' "$CODE" | tr a-z A-Z) row1= row2= row3=
        while [ -n "$code" ]; do
            rest=${code#?}; glyph "${code%"$rest"}"; code=$rest
            row1="$row1$a " row2="$row2$b " row3="$row3$c "
        done
        fg 15; at 16 3; t "$row1"; at 17 3; t "$row2"; at 18 3; t "$row3"
    fi
    bg 0
}

screen_draw()
{
    [ "$screen_ok" = 1 ] || return 0
    S="$E%G$E[?25l$E[9;0]$E[14;0]$PALETTE$E[0;37;40m$E[2J"
    if [ "$compact" = 0 ]; then
        hero
        fg 8; at 5 0; t "$(printf '%72s' '' | sed 's/ /─/g')"
        step 7 "$s1" 'Check hardware' "$d1"
        step 8 "$s2" "Register with ${host:-Lucia}" "$d2"
        step 9 "$s3" 'Owner approval' "$d3"
        step 10 "$s4" 'Install Debian 13' "$d4"
        step 11 "$s5" 'Join Lucia' "$d5"
        panel
        fg 15; at 21 0; t "$NOTE1"
        fg 14; at 22 0; t "$NOTE2"
        fg 8; at 23 0; t "$(net_line)"
    else
        y0=1 x0=1
        at 0 0; fg 15; t 'Lucia node setup'
        step 2 "$s1" 'Check hardware' "$d1"; step 3 "$s2" 'Register' "$d2"
        step 4 "$s3" 'Owner approval' "$d3"; step 5 "$s4" 'Install' "$d4"
        step 6 "$s5" 'Join Lucia' "$d5"
        fg 15; at 8 0
        if [ "$PANEL" = fail ]; then t "Stopped: $FAIL_WHAT"; at 9 0; t "$FAIL_TODO"
        elif [ -n "$CODE" ]; then t "Verification code: $CODE"; fi
        at 11 0; t "$NOTE1"; fg 14; at 12 0; t "$NOTE2"
    fi
    printf '%s' "$S" >"$SCREEN_TTY" 2>/dev/null
    S=
}

# Rewrite only the active step's detail, once a second, without a full redraw.
screen_detail()
{
    [ "$screen_ok" = 1 ] && [ "$compact" = 0 ] || return 0
    S=; at "$1" 44; fg 7; bg 0; t "$(printf '%-28s' "$2")"
    printf '%s' "$S" >"$SCREEN_TTY" 2>/dev/null
    S=
}

net_line()
{
    set -- $(ip -4 -o addr show scope global 2>/dev/null)
    [ -n "${4:-}" ] || { echo 'Waiting for a network address'; return; }
    mac=$(cat "/sys/class/net/$2/address" 2>/dev/null)
    echo "${4%/*}  ·  ${mac:-unknown MAC}  ·  $(uname -r)"
}

elapsed() { e=$(( $(date +%s) - $1 )); printf '%d:%02d' $((e / 60)) $((e % 60)); }

hardware_line()
{
    n=0
    for d in /sys/block/*; do
        case ${d##*/} in loop*|ram*|sr*|fd*|zram*|dm-*|md*|'*') ;; *) n=$((n + 1)) ;; esac
    done
    mem=$(awk '/^MemTotal:/ { printf "%d GB", ($2 + 524288) / 1048576 }' /proc/meminfo)
    [ "$n" = 1 ] && echo "1 disk  ·  $mem" || echo "$n disks  ·  $mem"
}

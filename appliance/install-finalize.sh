#!/bin/sh
# George Valdez · Ajustes del Edge 510 después del instalador oficial.
set -eu
target=${1:?Falta el punto de montaje del destino}
payload=${2:?Falta el directorio del paquete}
expected_serial=${3:?Falta la serie del destino}
[ "$target" = /mnt ] || exit 1
[ "$(kenv smbios.system.product)" = EDGE510 ] || exit 1
[ "$(geom disk list da1 | awk '/ident:/ {print $2}')" = "$expected_serial" ] || exit 1
mount -p | awk '$1 == "/dev/gpt/rootfs" && $2 == "/mnt" && $3 == "ufs" {ok=1} END {exit !ok}'
[ -f "$target/usr/local/etc/inc/config.inc" ] || exit 1
install -o root -g wheel -m 755 "$payload/04-edge510-pic" "$target/usr/local/etc/rc.syshook.d/early/04-edge510-pic"
cat >> "$target/boot/loader.conf.local" <<'EOF'

# Consola física del VeloCloud Edge 510 · George Valdez
console="comconsole"
comconsole_speed="115200"
comconsole_port="0x2f8"
hint.uart.0.flags="0"
hint.uart.1.flags="0x10"
EOF
printf '%s\n' '-S115200 -h' > "$target/boot.config"
/usr/local/bin/php -r 'require_once("config.inc"); $cnf=\OPNsense\Core\Config::getInstance()->lock(); try { if (file_put_contents($argv[1], (string)$cnf, LOCK_EX) === false) { throw new RuntimeException("No se pudo copiar la configuracion actual"); } } finally { $cnf->unlock(); }' "$target/conf/config.xml"
chroot "$target" /usr/local/bin/php -r 'require_once("util.inc"); require_once("config.inc"); $cnf=\OPNsense\Core\Config::getInstance()->lock(); parse_config(); $config["system"]["ssh"]["enabled"]="enabled"; $config["system"]["ssh"]["permitrootlogin"]="1"; $config["system"]["ssh"]["passwordauth"]="1"; write_config("George Valdez - administracion y verificacion de instalacion"); $cnf->unlock();'
grep -q 'comconsole_port="0x2f8"' "$target/boot/loader.conf.local"
test -x "$target/usr/local/etc/rc.syshook.d/early/04-edge510-pic"
if grep -q '/tmp/gv-install/install-finalize.sh' "$target/usr/libexec/bsdinstall/local.post-configure"; then
    rm "$target/usr/libexec/bsdinstall/local.post-configure"
fi
sync
echo "EDGE510_FINALIZADO"

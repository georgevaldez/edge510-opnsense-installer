<?php
require_once("config.inc");
require_once("interfaces.inc");
require_once("util.inc");
require_once("filter.inc");
require_once("system.inc");
$gateway = $argv[1] ?? '';
$dns = explode(',', $argv[2] ?? '');
if (!filter_var($gateway, FILTER_VALIDATE_IP, FILTER_FLAG_IPV4)) { exit(1); }
foreach ($dns as $server) {
    if (!filter_var($server, FILTER_VALIDATE_IP, FILTER_FLAG_IPV4)) { exit(1); }
}
$cnf = \OPNsense\Core\Config::getInstance()->lock();
parse_config();
$gw = new \OPNsense\Routing\Gateways();
$uuid = null;
foreach ($gw->gatewayIterator() as $item) {
    if ($item['name'] === 'GV_ADMIN_GW') {
        $uuid = $item['uuid'];
    }
}
$gw->createOrUpdateGateway([
    'disabled' => '0', 'descr' => 'Red de administracion',
    'defaultgw' => '1', 'ipprotocol' => 'inet', 'interface' => 'lan',
    'gateway' => $gateway, 'monitor_disable' => '1',
    'name' => 'GV_ADMIN_GW', 'weight' => '1', 'fargw' => '0'
], $uuid);
$config = \OPNsense\Core\Config::getInstance()->toArray(listtags());
$config['system']['dnsserver'] = $dns;
foreach (($config['dnsmasq']['dhcp_ranges'] ?? []) as $index => $range) {
    if (($range['interface'] ?? '') === 'lan') {
        unset($config['dnsmasq']['dhcp_ranges'][$index]);
    }
}
unset($config['dhcpd']['lan']['enable'], $config['dhcpdv6']['lan']['enable']);
write_config('George Valdez - salida a Internet para actualizaciones');
$cnf->unlock();
system_resolvconf_generate();
echo "RED_GUARDADA\n";

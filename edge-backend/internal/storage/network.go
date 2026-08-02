package storage

import (
	"net"
	"sort"
)

func localPrivateIPv4() string {
	var values []string
	interfaces, _ := net.Interfaces()
	for _, iface := range interfaces {
		if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 {
			continue
		}
		addresses, _ := iface.Addrs()
		for _, address := range addresses {
			if network, ok := address.(*net.IPNet); ok {
				if ip := network.IP.To4(); ip != nil && ip.IsPrivate() {
					values = append(values, ip.String())
				}
			}
		}
	}
	sort.Strings(values)
	if len(values) == 0 {
		return ""
	}
	return values[0]
}

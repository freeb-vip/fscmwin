package scanner

import "strings"

type scannerCommandKind int

const (
	scannerCommandOrder scannerCommandKind = iota
	scannerCommandStart
	scannerCommandEnd
)

type scannerCommand struct {
	kind          scannerCommandKind
	operationCode string
}

func parseScannerCommand(payload string) scannerCommand {
	value := strings.ToUpper(strings.TrimSpace(payload))
	if value == "FSCM_JOB:END" {
		return scannerCommand{kind: scannerCommandEnd}
	}
	const prefix = "FSCM_JOB:"
	if strings.HasPrefix(value, prefix) {
		code := strings.TrimSpace(strings.TrimPrefix(value, prefix))
		if code != "" {
			return scannerCommand{kind: scannerCommandStart, operationCode: code}
		}
	}
	return scannerCommand{kind: scannerCommandOrder}
}

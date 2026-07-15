package catalog

import (
	"crypto/ed25519"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

type ticketClaims struct {
	NamespaceID uint   `json:"ns"`
	NodeID      string `json:"node"`
	Scope       string `json:"scope"`
	Issuer      string `json:"iss"`
	ExpiresAt   int64  `json:"exp"`
}

func validateTicket(token string, publicKey ed25519.PublicKey, namespaceID uint, nodeID string) error {
	if len(publicKey) != ed25519.PublicKeySize {
		return fmt.Errorf("edge ticket key is unavailable")
	}
	parts := strings.Split(strings.TrimSpace(token), ".")
	if len(parts) != 3 {
		return fmt.Errorf("edge ticket is missing")
	}
	header, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil {
		return fmt.Errorf("invalid edge ticket")
	}
	var metadata struct {
		Algorithm string `json:"alg"`
	}
	if json.Unmarshal(header, &metadata) != nil || metadata.Algorithm != "EdDSA" {
		return fmt.Errorf("invalid edge ticket algorithm")
	}
	signature, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil || !ed25519.Verify(publicKey, []byte(parts[0]+"."+parts[1]), signature) {
		return fmt.Errorf("invalid edge ticket signature")
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return fmt.Errorf("invalid edge ticket")
	}
	var claims ticketClaims
	if err := json.Unmarshal(payload, &claims); err != nil {
		return fmt.Errorf("invalid edge ticket")
	}
	if claims.Issuer != "fscm-edge-catalog" || claims.Scope != "catalog:read" || claims.NamespaceID != namespaceID || claims.NodeID != nodeID || claims.ExpiresAt <= time.Now().Unix() {
		return fmt.Errorf("edge ticket is expired or unauthorized")
	}
	return nil
}

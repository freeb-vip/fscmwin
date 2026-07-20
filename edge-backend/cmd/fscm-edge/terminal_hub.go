package main

import (
	"errors"
	"fmt"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
	"golang.org/x/net/websocket"
)

const (
	terminalProtocolVersion = 1
	terminalPingInterval    = 20 * time.Second
	terminalReadTimeout     = 60 * time.Second
	terminalCommandTimeout  = 3 * time.Second
	findDeviceDuration      = 60
)

var (
	errTerminalNotFound   = errors.New("terminal not found")
	errTerminalOffline    = errors.New("terminal offline")
	errFindDeviceDisabled = errors.New("terminal does not support find-device")
	errTerminalTimeout    = errors.New("terminal command timed out")
)

type terminal struct {
	TerminalID   string    `json:"terminal_id,omitempty"`
	Name         string    `json:"name"`
	IP           string    `json:"ip"`
	UserAgent    string    `json:"user_agent"`
	Source       string    `json:"source"`
	AppVersion   string    `json:"app_version,omitempty"`
	Platform     string    `json:"platform,omitempty"`
	Capabilities []string  `json:"capabilities,omitempty"`
	ConnectedAt  time.Time `json:"connected_at,omitempty"`
	LastSeenAt   time.Time `json:"last_seen_at"`
	Status       string    `json:"status"`
	Finding      bool      `json:"finding"`
	CommandID    string    `json:"command_id,omitempty"`
}

type terminalRegisterMessage struct {
	Type            string   `json:"type"`
	ProtocolVersion int      `json:"protocol_version"`
	TerminalID      string   `json:"terminal_id"`
	TerminalName    string   `json:"terminal_name"`
	AppVersion      string   `json:"app_version"`
	Platform        string   `json:"platform"`
	LANIP           string   `json:"lan_ip"`
	Capabilities    []string `json:"capabilities"`
}

type terminalMessage struct {
	Type      string `json:"type"`
	CommandID string `json:"command_id,omitempty"`
	Status    string `json:"status,omitempty"`
	Message   string `json:"message,omitempty"`
}

type terminalCommand struct {
	Type            string    `json:"type"`
	ProtocolVersion int       `json:"protocol_version"`
	CommandID       string    `json:"command_id"`
	TargetCommandID string    `json:"target_command_id,omitempty"`
	Action          string    `json:"action"`
	DurationSeconds int       `json:"duration_seconds,omitempty"`
	IssuedAt        time.Time `json:"issued_at"`
}

type terminalCommandResult struct {
	CommandID string `json:"command_id"`
	Status    string `json:"status"`
	Message   string `json:"message,omitempty"`
}

type terminalConnection struct {
	ws      *websocket.Conn
	writeMu sync.Mutex
}

func (c *terminalConnection) send(value interface{}) error {
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	return websocket.JSON.Send(c.ws, value)
}

type terminalStore struct {
	sync.RWMutex
	items   map[string]terminal
	clients map[string]*terminalConnection
	pending map[string]chan terminalCommandResult
}

func newTerminalStore() *terminalStore {
	return &terminalStore{
		items:   make(map[string]terminal),
		clients: make(map[string]*terminalConnection),
		pending: make(map[string]chan terminalCommandResult),
	}
}

func (s *terminalStore) connect(ws *websocket.Conn, ip, userAgent string) {
	defer ws.Close()
	_ = ws.SetDeadline(time.Now().Add(10 * time.Second))
	var registration terminalRegisterMessage
	if err := websocket.JSON.Receive(ws, &registration); err != nil {
		return
	}
	registration.TerminalID = strings.TrimSpace(registration.TerminalID)
	if registration.Type != "register" || registration.ProtocolVersion != terminalProtocolVersion || registration.TerminalID == "" {
		_ = websocket.JSON.Send(ws, terminalMessage{Type: "error", Message: "invalid terminal registration"})
		return
	}

	now := time.Now()
	client := &terminalConnection{ws: ws}
	value := terminal{
		TerminalID: registration.TerminalID,
		Name:       first(registration.TerminalName, registration.TerminalID),
		IP:         first(registration.LANIP, ip), UserAgent: userAgent, Source: "websocket",
		AppVersion: strings.TrimSpace(registration.AppVersion), Platform: strings.TrimSpace(registration.Platform),
		Capabilities: normalizedCapabilities(registration.Capabilities), ConnectedAt: now,
		LastSeenAt: now, Status: "online",
	}
	s.Lock()
	previous := s.clients[value.TerminalID]
	s.clients[value.TerminalID] = client
	s.items[value.TerminalID] = value
	s.Unlock()
	if previous != nil {
		_ = previous.ws.Close()
	}
	defer s.disconnect(value.TerminalID, client)

	_ = ws.SetDeadline(time.Time{})
	if err := client.send(map[string]interface{}{
		"type": "registered", "protocol_version": terminalProtocolVersion,
		"heartbeat_interval_seconds": int(terminalPingInterval / time.Second),
	}); err != nil {
		return
	}

	done := make(chan struct{})
	go func() {
		defer close(done)
		ticker := time.NewTicker(terminalPingInterval)
		defer ticker.Stop()
		for range ticker.C {
			if err := client.send(terminalMessage{Type: "ping"}); err != nil {
				return
			}
		}
	}()

	for {
		_ = ws.SetReadDeadline(time.Now().Add(terminalReadTimeout))
		var message terminalMessage
		if err := websocket.JSON.Receive(ws, &message); err != nil {
			return
		}
		s.markSeen(value.TerminalID, client)
		switch message.Type {
		case "pong", "heartbeat":
		case "command_result":
			s.completeCommand(value.TerminalID, terminalCommandResult{
				CommandID: strings.TrimSpace(message.CommandID),
				Status:    strings.TrimSpace(message.Status), Message: strings.TrimSpace(message.Message),
			})
		}
		select {
		case <-done:
			return
		default:
		}
	}
}

func (s *terminalStore) disconnect(terminalID string, client *terminalConnection) {
	s.Lock()
	defer s.Unlock()
	if s.clients[terminalID] != client {
		return
	}
	delete(s.clients, terminalID)
	value := s.items[terminalID]
	value.Status, value.Finding, value.CommandID = "offline", false, ""
	value.LastSeenAt = time.Now()
	s.items[terminalID] = value
}

func (s *terminalStore) markSeen(terminalID string, client *terminalConnection) {
	s.Lock()
	defer s.Unlock()
	if s.clients[terminalID] != client {
		return
	}
	value := s.items[terminalID]
	value.LastSeenAt, value.Status = time.Now(), "online"
	s.items[terminalID] = value
}

func (s *terminalStore) completeCommand(terminalID string, result terminalCommandResult) {
	if result.CommandID == "" {
		return
	}
	s.Lock()
	value := s.items[terminalID]
	switch result.Status {
	case "playing":
		value.Finding, value.CommandID = true, result.CommandID
	case "stopped", "expired", "failed":
		if result.Status == "stopped" || value.CommandID == "" || value.CommandID == result.CommandID {
			value.Finding, value.CommandID = false, ""
		}
	}
	value.LastSeenAt = time.Now()
	s.items[terminalID] = value
	waiter := s.pending[result.CommandID]
	delete(s.pending, result.CommandID)
	s.Unlock()
	if waiter != nil {
		select {
		case waiter <- result:
		default:
		}
	}
}

func (s *terminalStore) find(terminalID string) (terminalCommandResult, error) {
	return s.dispatch(terminalID, "find_device", findDeviceDuration, "")
}

func (s *terminalStore) stopFind(terminalID string) (terminalCommandResult, error) {
	s.RLock()
	activeCommandID := s.items[terminalID].CommandID
	s.RUnlock()
	return s.dispatch(terminalID, "stop_find_device", 0, activeCommandID)
}

func (s *terminalStore) dispatch(terminalID, action string, duration int, targetCommandID string) (terminalCommandResult, error) {
	terminalID = strings.TrimSpace(terminalID)
	s.Lock()
	value, exists := s.items[terminalID]
	client := s.clients[terminalID]
	if !exists {
		s.Unlock()
		return terminalCommandResult{}, errTerminalNotFound
	}
	if client == nil || value.Status != "online" {
		s.Unlock()
		return terminalCommandResult{}, errTerminalOffline
	}
	if !containsCapability(value.Capabilities, "find-device") {
		s.Unlock()
		return terminalCommandResult{}, errFindDeviceDisabled
	}
	commandID := uuid.NewString()
	waiter := make(chan terminalCommandResult, 1)
	s.pending[commandID] = waiter
	s.Unlock()

	command := terminalCommand{
		Type: "command", ProtocolVersion: terminalProtocolVersion, CommandID: commandID,
		TargetCommandID: targetCommandID, Action: action, DurationSeconds: duration, IssuedAt: time.Now().UTC(),
	}
	if err := client.send(command); err != nil {
		s.Lock()
		delete(s.pending, commandID)
		s.Unlock()
		return terminalCommandResult{}, fmt.Errorf("send terminal command: %w", err)
	}
	select {
	case result := <-waiter:
		return result, nil
	case <-time.After(terminalCommandTimeout):
		s.Lock()
		delete(s.pending, commandID)
		s.Unlock()
		return terminalCommandResult{}, errTerminalTimeout
	}
}

func (s *terminalStore) recordProbe(ip, userAgent, name string) {
	value := terminal{Name: first(name, ip), IP: ip, UserAgent: userAgent, Source: "probe", LastSeenAt: time.Now(), Status: "online"}
	s.Lock()
	s.items["probe:"+ip+"|"+userAgent] = value
	s.Unlock()
}

func (s *terminalStore) list() []terminal {
	s.RLock()
	defer s.RUnlock()
	result := make([]terminal, 0, len(s.items))
	for _, value := range s.items {
		result = append(result, value)
	}
	sort.Slice(result, func(i, j int) bool {
		if result[i].Status != result[j].Status {
			return result[i].Status == "online"
		}
		return result[i].Name < result[j].Name
	})
	return result
}

func normalizedCapabilities(values []string) []string {
	result := make([]string, 0, len(values))
	seen := make(map[string]struct{}, len(values))
	for _, value := range values {
		value = strings.ToLower(strings.TrimSpace(value))
		if value == "" {
			continue
		}
		if _, ok := seen[value]; ok {
			continue
		}
		seen[value] = struct{}{}
		result = append(result, value)
	}
	return result
}

func containsCapability(values []string, expected string) bool {
	for _, value := range values {
		if strings.EqualFold(value, expected) {
			return true
		}
	}
	return false
}

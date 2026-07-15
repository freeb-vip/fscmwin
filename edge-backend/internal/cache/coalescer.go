package cache

import "sync"

type Result struct {
	Response Response
	Err      error
}

type call struct {
	done   chan struct{}
	result Result
}

type Coalescer struct {
	mu    sync.Mutex
	calls map[string]*call
}

func NewCoalescer() *Coalescer { return &Coalescer{calls: make(map[string]*call)} }

func (c *Coalescer) Do(key string, fn func() Result) (Result, bool) {
	c.mu.Lock()
	if existing, ok := c.calls[key]; ok {
		c.mu.Unlock()
		<-existing.done
		return existing.result, true
	}
	current := &call{done: make(chan struct{})}
	c.calls[key] = current
	c.mu.Unlock()

	current.result = fn()
	close(current.done)
	c.mu.Lock()
	delete(c.calls, key)
	c.mu.Unlock()
	return current.result, false
}

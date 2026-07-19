package printing

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"os"
	"strings"
	"sync"
	"time"
)

var ErrIdempotencyConflict = errors.New("idempotency key already belongs to a different print request")

type Config struct {
	DefaultPrinter, Template, Orientation, Mode string
	WidthMM, HeightMM                           float64
	Copies                                      int
	TemplatesPath                               string
	JobsPath                                    string
	QRCodePrefix                                string
}
type Template struct {
	ID                 string  `json:"Id"`
	TemplateNumber     string  `json:"TemplateNumber,omitempty"`
	Name               string  `json:"Name"`
	Printer            string  `json:"Printer"`
	WidthMillimeters   float64 `json:"WidthMillimeters"`
	HeightMillimeters  float64 `json:"HeightMillimeters"`
	Orientation        string  `json:"Orientation"`
	Mode               string  `json:"Mode"`
	Copies             int     `json:"Copies"`
	OffsetXMillimeters float64 `json:"OffsetXMillimeters"`
	SkuQRPrefix        string  `json:"SkuQrPrefix"`
	LabelQRPrefix      string  `json:"LabelQrPrefix,omitempty"`
	LayoutStyle        string  `json:"LayoutStyle,omitempty"`
	TextFontSizePoints float64 `json:"TextFontSizePoints,omitempty"`
	Type               string  `json:"Type,omitempty"`
	MaxDisplayLength   int     `json:"MaxDisplayLength,omitempty"`
}
type Job struct {
	ID                 string          `json:"id"`
	IdempotencyKey     string          `json:"idempotency_key"`
	Source             string          `json:"source"`
	Printer            string          `json:"printer"`
	TemplateID         string          `json:"template_id,omitempty"`
	Template           string          `json:"template"`
	Status             string          `json:"status"`
	SubmittedAt        time.Time       `json:"submitted_at"`
	StartedAt          *time.Time      `json:"started_at,omitempty"`
	FinishedAt         *time.Time      `json:"finished_at,omitempty"`
	Error              string          `json:"error,omitempty"`
	Items              []Item          `json:"items,omitempty"`
	Kind               string          `json:"kind,omitempty"`
	Text               string          `json:"text,omitempty"`
	QRCodeContent      string          `json:"qr_code_content,omitempty"`
	PayloadSnapshot    json.RawMessage `json:"payload_snapshot,omitempty"`
	RequestFingerprint string          `json:"request_fingerprint,omitempty"`
	Copies             int             `json:"copies"`
	BoxMarks           []BoxMark       `json:"box_marks,omitempty"`
	RemoteJobID        uint            `json:"-"`
	RemoteLeaseToken   string          `json:"-"`
}
type Item struct {
	SKUID         uint   `json:"sku_id"`
	SKUCode       string `json:"sku_code"`
	Quantity      int    `json:"quantity"`
	QRCodeContent string `json:"qr_code_content"`
}
type BoxMark struct {
	BoxPlanID    uint     `json:"box_plan_id"`
	SeaMark      string   `json:"sea_mark"`
	Shop         string   `json:"shop"`
	StyleCode    string   `json:"style_code"`
	SKULines     []string `json:"sku_lines"`
	Spec         string   `json:"spec"`
	PCS          string   `json:"pcs"`
	SKUBoxs      string   `json:"sku_boxs"`
	Batch        string   `json:"batch"`
	SKUQRPayload string   `json:"sku_qr_payload"`
	BoxQRPayload string   `json:"box_qr_payload"`
	BoxUID       string   `json:"box_uid"`
	InboundCode  string   `json:"inbound_code"`
}
type Request struct {
	JobID           string          `json:"job_id"`
	IdempotencyKey  string          `json:"idempotency_key"`
	Source          string          `json:"source"`
	Printer         string          `json:"printer"`
	Template        string          `json:"template"`
	TemplateID      string          `json:"template_id"`
	Items           []Item          `json:"items"`
	Kind            string          `json:"kind"`
	Text            string          `json:"text"`
	QRCodeContent   string          `json:"qr_code_content"`
	PayloadSnapshot json.RawMessage `json:"payload_snapshot"`
	Copies          int             `json:"copies"`
	BoxMarks        []BoxMark       `json:"box_marks"`
}

type Service struct {
	cfg           Config
	mu            sync.RWMutex
	jobs          []Job
	keys          map[string]string
	templatesPath string
	store         *Store
}

func New(cfg Config) (*Service, error) {
	if cfg.Copies < 1 {
		cfg.Copies = 1
	}
	store, jobs, err := OpenStore(cfg.JobsPath)
	if err != nil {
		return nil, err
	}
	keys := make(map[string]string)
	for _, job := range jobs {
		if job.IdempotencyKey != "" {
			keys[job.IdempotencyKey] = job.ID
		}
	}
	return &Service{cfg: cfg, jobs: jobs, keys: keys, templatesPath: cfg.TemplatesPath, store: store}, nil
}

func (s *Service) Close() error   { return s.store.Close() }
func (s *Service) Config() Config { return s.cfg }
func (s *Service) Jobs() []Job {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return append([]Job(nil), s.jobs...)
}

func (s *Service) ListJobs(status string, page, pageSize int) ([]Job, int) {
	if page < 1 {
		page = 1
	}
	if pageSize < 1 || pageSize > 200 {
		pageSize = 50
	}
	status = strings.TrimSpace(status)
	s.mu.RLock()
	defer s.mu.RUnlock()
	filtered := make([]Job, 0, len(s.jobs))
	for _, job := range s.jobs {
		if status == "" || job.Status == status {
			filtered = append(filtered, job)
		}
	}
	total := len(filtered)
	start := (page - 1) * pageSize
	if start >= total {
		return []Job{}, total
	}
	end := start + pageSize
	if end > total {
		end = total
	}
	return append([]Job(nil), filtered[start:end]...), total
}
func (s *Service) Templates() []Template {
	if s.templatesPath == "" {
		return nil
	}
	data, err := os.ReadFile(s.templatesPath)
	if err != nil {
		return nil
	}
	var templates []Template
	if json.Unmarshal(data, &templates) != nil {
		return nil
	}
	return templates
}
func (s *Service) HasTemplate(id string) bool {
	for _, template := range s.Templates() {
		if template.ID == id {
			return true
		}
	}
	return false
}
func (s *Service) SaveTemplate(template Template) error {
	templates := s.Templates()
	found := false
	for i := range templates {
		if templates[i].ID == template.ID {
			templates[i] = template
			found = true
			break
		}
	}
	if !found {
		templates = append(templates, template)
	}
	return s.writeTemplates(templates)
}
func (s *Service) DeleteTemplate(id string) error {
	templates := make([]Template, 0)
	for _, template := range s.Templates() {
		if template.ID != id {
			templates = append(templates, template)
		}
	}
	return s.writeTemplates(templates)
}
func (s *Service) writeTemplates(templates []Template) error {
	data, err := json.MarshalIndent(templates, "", "  ")
	if err != nil || s.templatesPath == "" {
		return err
	}
	return os.WriteFile(s.templatesPath, data, 0o600)
}
func (s *Service) Find(id string) (Job, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	for _, job := range s.jobs {
		if job.ID == id {
			return job, true
		}
	}
	return Job{}, false
}
func (s *Service) SetStatus(id, status string) (Job, bool, error) {
	return s.SetStatusWithError(id, status, "")
}

func (s *Service) SetStatusWithError(id, status, jobError string) (Job, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for i := range s.jobs {
		if s.jobs[i].ID == id {
			job := s.jobs[i]
			job.Status = status
			job.Error = jobError
			now := time.Now()
			if status == "printing" {
				job.StartedAt = &now
			}
			if status == "completed" || status == "failed" || status == "cancelled" {
				job.FinishedAt = &now
			}
			if err := s.store.Save(job, job.RemoteJobID == 0); err != nil {
				return Job{}, false, err
			}
			s.jobs[i] = job
			return job, true, nil
		}
	}
	return Job{}, false, nil
}
func (s *Service) Create(req Request) (Job, bool, error) {
	return s.create(req, 0, "")
}

// CreateRemote preserves the center lease so the Windows print worker can report its final result.
func (s *Service) CreateRemote(req Request, remoteJobID uint, leaseToken string) (Job, bool, error) {
	return s.create(req, remoteJobID, leaseToken)
}

func (s *Service) create(req Request, remoteJobID uint, leaseToken string) (Job, bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	printer, template := req.Printer, req.Template
	qrPrefix := s.cfg.QRCodePrefix
	if req.TemplateID != "" {
		for _, profile := range s.Templates() {
			if profile.ID == req.TemplateID {
				printer, template = profile.Printer, profile.Name
				qrPrefix = profile.SkuQRPrefix
				break
			}
		}
	}
	key := strings.TrimSpace(req.IdempotencyKey)
	fingerprint := fingerprintPrintRequest(req)
	if key != "" {
		if id, ok := s.keys[key]; ok {
			for _, job := range s.jobs {
				if job.ID == id {
					if job.RequestFingerprint != "" && job.RequestFingerprint != fingerprint {
						return Job{}, false, ErrIdempotencyConflict
					}
					return job, true, nil
				}
			}
		}
	}
	id := strings.TrimSpace(req.JobID)
	if id == "" {
		var value [8]byte
		_, _ = rand.Read(value[:])
		id = "print-" + hex.EncodeToString(value[:])
	}
	copies := req.Copies
	if copies < 1 {
		copies = 1
	}
	job := Job{ID: id, IdempotencyKey: key, Source: first(req.Source, "lan"), Printer: first(printer, s.cfg.DefaultPrinter), TemplateID: req.TemplateID, Template: first(template, s.cfg.Template), Status: "queued", SubmittedAt: time.Now(), RemoteJobID: remoteJobID, RemoteLeaseToken: leaseToken, Kind: req.Kind, Text: strings.TrimSpace(req.Text), QRCodeContent: strings.TrimSpace(req.QRCodeContent), PayloadSnapshot: append(json.RawMessage(nil), req.PayloadSnapshot...), RequestFingerprint: fingerprint, Copies: copies, BoxMarks: req.BoxMarks}
	job.Items = make([]Item, 0, len(req.Items))
	for _, item := range req.Items {
		item.SKUCode = strings.TrimSpace(item.SKUCode)
		if strings.TrimSpace(item.QRCodeContent) == "" {
			item.QRCodeContent = first(qrPrefix, "T") + item.SKUCode
		}
		if item.Quantity < 1 {
			item.Quantity = 1
		}
		job.Items = append(job.Items, item)
	}
	if err := s.store.Save(job, remoteJobID == 0); err != nil {
		return Job{}, false, err
	}
	s.jobs = append([]Job{job}, s.jobs...)
	if key != "" {
		s.keys[key] = id
	}
	return job, false, nil
}

func fingerprintPrintRequest(req Request) string {
	payload, _ := json.Marshal(struct {
		Printer         string          `json:"printer"`
		Template        string          `json:"template"`
		TemplateID      string          `json:"template_id"`
		Kind            string          `json:"kind"`
		Text            string          `json:"text"`
		QRCodeContent   string          `json:"qr_code_content"`
		PayloadSnapshot json.RawMessage `json:"payload_snapshot"`
		Copies          int             `json:"copies"`
		Items           []Item          `json:"items"`
		BoxMarks        []BoxMark       `json:"box_marks"`
	}{req.Printer, req.Template, req.TemplateID, req.Kind, req.Text, req.QRCodeContent, req.PayloadSnapshot, req.Copies, req.Items, req.BoxMarks})
	sum := sha256.Sum256(payload)
	return hex.EncodeToString(sum[:])
}

func (s *Service) PendingAudits(limit int) ([]Job, error) { return s.store.PendingAudits(limit) }

func (s *Service) MarkAuditSynced(jobID string) error { return s.store.MarkAuditSynced(jobID) }

func (s *Service) MarkAuditFailed(jobID string, err error) error {
	return s.store.MarkAuditFailed(jobID, err)
}
func first(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
	}
	return ""
}

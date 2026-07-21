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

const MaxCopiesPerContent = 5

var (
	ErrIdempotencyConflict = errors.New("idempotency key already belongs to a different print request")
	ErrPrintCopiesExceeded = errors.New("print protection blocked more than 5 copies of the same content")
)

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
	SortOrder          int     `json:"SortOrder,omitempty"`
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
	RemoteBatchID      *uint           `json:"remote_batch_id,omitempty"`
	RemoteSequenceNo   int             `json:"remote_sequence_no,omitempty"`
	RemoteJobType      string          `json:"remote_job_type,omitempty"`
	RemoteAttemptCount int             `json:"remote_attempt_count,omitempty"`
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
	SKUCode      string   `json:"sku_code"`
	SKUName      string   `json:"sku_name"`
	QtyPerBox    int      `json:"qty_per_box"`
	BoxQRPayload string   `json:"box_qr_payload"`
	BoxUID       string   `json:"box_uid"`
	InboundCode  string   `json:"inbound_code"`
}
type Request struct {
	JobID              string          `json:"job_id"`
	IdempotencyKey     string          `json:"idempotency_key"`
	Source             string          `json:"source"`
	Printer            string          `json:"printer"`
	Template           string          `json:"template"`
	TemplateID         string          `json:"template_id"`
	Items              []Item          `json:"items"`
	Kind               string          `json:"kind"`
	Text               string          `json:"text"`
	QRCodeContent      string          `json:"qr_code_content"`
	PayloadSnapshot    json.RawMessage `json:"payload_snapshot"`
	Copies             int             `json:"copies"`
	BoxMarks           []BoxMark       `json:"box_marks"`
	RemoteBatchID      *uint           `json:"remote_batch_id,omitempty"`
	RemoteSequenceNo   int             `json:"remote_sequence_no,omitempty"`
	RemoteJobType      string          `json:"remote_job_type,omitempty"`
	RemoteAttemptCount int             `json:"remote_attempt_count,omitempty"`
}

type Service struct {
	cfg            Config
	mu             sync.RWMutex
	jobs           []Job
	keys           map[string]string
	templatesPath  string
	store          *Store
	remoteClaiming bool
	preferredBatch uint
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
	service := &Service{cfg: cfg, jobs: jobs, keys: keys, templatesPath: cfg.TemplatesPath, store: store}
	for _, job := range jobs {
		if job.RemoteBatchID != nil && job.RemoteJobID > 0 && (job.Status == "queued" || job.Status == "printing") {
			service.preferredBatch = *job.RemoteBatchID
			break
		}
	}
	if service.preferredBatch == 0 {
		for _, job := range jobs {
			if job.RemoteBatchID != nil && job.RemoteJobID > 0 {
				service.preferredBatch = *job.RemoteBatchID
				break
			}
		}
	}
	return service, nil
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
			// A queued snapshot may still be held by the Windows worker when an
			// operator stops its batch. Keep terminal states sticky so that stale
			// workers cannot restart or overwrite a cancelled job.
			if job.Status == "cancelled" && status != "cancelled" {
				return job, true, nil
			}
			if isTerminalPrintStatus(job.Status) && status != "queued" {
				return job, true, nil
			}
			if status == "printing" && job.Status != "queued" {
				return job, true, nil
			}
			job.Status = status
			job.Error = jobError
			now := time.Now()
			if status == "printing" {
				job.StartedAt = &now
			}
			if status == "completed" || status == "failed" || status == "cancelled" {
				job.FinishedAt = &now
			}
			var err error
			if job.RemoteJobID > 0 && (status == "completed" || status == "failed" || status == "cancelled" || status == "interrupted") {
				err = s.store.SaveWithRemoteCompletion(job, remoteCompletionForJob(job))
			} else {
				err = s.store.Save(job, job.RemoteJobID == 0)
			}
			if err != nil {
				return Job{}, false, err
			}
			s.jobs[i] = job
			return job, true, nil
		}
	}
	return Job{}, false, nil
}

// CancelRemoteBatch stops any center task from the batch that has already
// reached this edge. Center-side cancellation separately prevents new leases.
func (s *Service) CancelRemoteBatch(batchID uint) ([]Job, error) {
	if batchID == 0 {
		return nil, errors.New("remote batch id is required")
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	affected := make([]Job, 0, 1)
	for i := range s.jobs {
		job := s.jobs[i]
		if job.RemoteBatchID == nil || *job.RemoteBatchID != batchID || (job.Status != "queued" && job.Status != "printing") {
			continue
		}
		now := time.Now()
		job.Status = "cancelled"
		job.Error = "批量打印已由操作员停止"
		job.FinishedAt = &now
		if err := s.store.SaveWithRemoteCompletion(job, remoteCompletionForJob(job)); err != nil {
			return affected, err
		}
		s.jobs[i] = job
		affected = append(affected, job)
	}
	if s.preferredBatch == batchID {
		s.preferredBatch = 0
	}
	return affected, nil
}
func (s *Service) Create(req Request) (Job, bool, error) {
	return s.create(req, 0, "")
}

// CreateRemote preserves the center lease so the Windows print worker can report its final result.
func (s *Service) CreateRemote(req Request, remoteJobID uint, leaseToken string) (Job, bool, error) {
	job, duplicate, err := s.create(req, remoteJobID, leaseToken)
	if err == nil && req.RemoteBatchID != nil {
		s.SetPreferredRemoteBatchID(*req.RemoteBatchID)
	}
	return job, duplicate, err
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
			for index := range s.jobs {
				if s.jobs[index].ID == id {
					job := s.jobs[index]
					if job.RequestFingerprint != "" && job.RequestFingerprint != fingerprint {
						return Job{}, false, ErrIdempotencyConflict
					}
					if remoteJobID > 0 && job.RemoteJobID == remoteJobID {
						job.RemoteLeaseToken = leaseToken
						job.RemoteBatchID = req.RemoteBatchID
						job.RemoteSequenceNo = req.RemoteSequenceNo
						job.RemoteJobType = req.RemoteJobType
						job.RemoteAttemptCount = req.RemoteAttemptCount
						var err error
						if isTerminalPrintStatus(job.Status) {
							err = s.store.SaveWithRemoteCompletion(job, remoteCompletionForJob(job))
						} else {
							err = s.store.Save(job, false)
						}
						if err != nil {
							return Job{}, false, err
						}
						s.jobs[index] = job
					}
					return job, true, nil
				}
			}
		}
	}
	protectedRequest := req
	protectedRequest.Items = append([]Item(nil), req.Items...)
	for index := range protectedRequest.Items {
		item := &protectedRequest.Items[index]
		if strings.TrimSpace(item.QRCodeContent) == "" {
			item.QRCodeContent = first(qrPrefix, "T") + strings.TrimSpace(item.SKUCode)
		}
	}
	requestedCopies, err := requestContentCopies(protectedRequest)
	if err != nil {
		return Job{}, false, err
	}
	if s.exceedsActiveOrBatchCopyLimit(req.RemoteBatchID, requestedCopies) {
		return Job{}, false, ErrPrintCopiesExceeded
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
	job := Job{ID: id, IdempotencyKey: key, Source: first(req.Source, "lan"), Printer: first(printer, s.cfg.DefaultPrinter), TemplateID: req.TemplateID, Template: first(template, s.cfg.Template), Status: "queued", SubmittedAt: time.Now(), RemoteJobID: remoteJobID, RemoteLeaseToken: leaseToken, RemoteBatchID: req.RemoteBatchID, RemoteSequenceNo: req.RemoteSequenceNo, RemoteJobType: req.RemoteJobType, RemoteAttemptCount: req.RemoteAttemptCount, Kind: req.Kind, Text: strings.TrimSpace(req.Text), QRCodeContent: strings.TrimSpace(req.QRCodeContent), PayloadSnapshot: append(json.RawMessage(nil), req.PayloadSnapshot...), RequestFingerprint: fingerprint, Copies: copies, BoxMarks: req.BoxMarks}
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

func requestContentCopies(req Request) (map[string]int, error) {
	result := make(map[string]int)
	add := func(content string, copies int) error {
		if copies < 1 {
			copies = 1
		}
		if copies > MaxCopiesPerContent {
			return ErrPrintCopiesExceeded
		}
		content = strings.TrimSpace(content)
		if content == "" {
			return nil
		}
		result[content] += copies
		if result[content] > MaxCopiesPerContent {
			return ErrPrintCopiesExceeded
		}
		return nil
	}

	if req.Copies > MaxCopiesPerContent {
		return nil, ErrPrintCopiesExceeded
	}
	kind := strings.ToLower(strings.TrimSpace(req.Kind))
	if kind == "manual_text" || kind == "batch_content" {
		if err := add(first(req.QRCodeContent, req.Text), req.Copies); err != nil {
			return nil, err
		}
		return result, nil
	}
	if kind == "manufacturer_box_mark" {
		for _, mark := range req.BoxMarks {
			identity := first(mark.BoxQRPayload, mark.BoxUID, mark.InboundCode, mark.Shop, mark.SKUQRPayload, mark.SKUCode)
			if identity == "" {
				payload, _ := json.Marshal(mark)
				identity = string(payload)
			}
			if err := add("box-mark:"+identity, req.Copies); err != nil {
				return nil, err
			}
		}
		return result, nil
	}
	for _, item := range req.Items {
		content := first(strings.TrimSpace(item.QRCodeContent), strings.TrimSpace(item.SKUCode))
		if err := add(content, item.Quantity); err != nil {
			return nil, err
		}
	}
	if len(result) == 0 {
		if err := add(first(req.QRCodeContent, req.Text), req.Copies); err != nil {
			return nil, err
		}
	}
	return result, nil
}

func jobContentCopies(job Job) map[string]int {
	result, _ := requestContentCopies(Request{
		Kind: job.Kind, Text: job.Text, QRCodeContent: job.QRCodeContent, Copies: job.Copies,
		Items: job.Items, BoxMarks: job.BoxMarks,
	})
	return result
}

func (s *Service) exceedsActiveOrBatchCopyLimit(batchID *uint, requested map[string]int) bool {
	accumulated := make(map[string]int)
	for _, job := range s.jobs {
		sameBatch := batchID != nil && job.RemoteBatchID != nil && *batchID == *job.RemoteBatchID
		active := job.Status == "queued" || job.Status == "printing"
		if !sameBatch && !active {
			continue
		}
		for content, copies := range jobContentCopies(job) {
			accumulated[content] += copies
		}
	}
	for content, copies := range requested {
		if accumulated[content]+copies > MaxCopiesPerContent {
			return true
		}
	}
	return false
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

func (s *Service) BeginRemoteClaim() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.remoteClaiming {
		return false
	}
	for _, job := range s.jobs {
		if job.Status == "queued" || job.Status == "printing" {
			return false
		}
	}
	s.remoteClaiming = true
	return true
}

func (s *Service) EndRemoteClaim() {
	s.mu.Lock()
	s.remoteClaiming = false
	s.mu.Unlock()
}

func (s *Service) PreferredRemoteBatchID() uint {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.preferredBatch
}

func (s *Service) SetPreferredRemoteBatchID(batchID uint) bool {
	if batchID == 0 {
		return false
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.preferredBatch != 0 && s.preferredBatch != batchID {
		return false
	}
	s.preferredBatch = batchID
	return true
}

func (s *Service) ClearPreferredRemoteBatchID(batchID uint) {
	s.mu.Lock()
	if batchID == 0 || s.preferredBatch == batchID {
		s.preferredBatch = 0
	}
	s.mu.Unlock()
}

func (s *Service) EnqueueRemoteFailure(remoteJobID uint, leaseToken, errorCode, errorMessage string, result interface{}) error {
	payload, err := json.Marshal(result)
	if err != nil {
		return err
	}
	return s.store.EnqueueRemoteCompletion(RemoteCompletion{RemoteJobID: remoteJobID, LeaseToken: leaseToken, Status: "failed", ErrorCode: errorCode, ErrorMessage: errorMessage, Result: payload})
}

func (s *Service) PendingRemoteCompletions(limit int) ([]RemoteCompletion, error) {
	return s.store.PendingRemoteCompletions(limit)
}

func (s *Service) MarkRemoteCompletionSynced(remoteJobID uint) error {
	return s.store.MarkRemoteCompletionSynced(remoteJobID)
}

func (s *Service) MarkRemoteCompletionFailed(remoteJobID uint, syncErr error, retryAt time.Time) error {
	return s.store.MarkRemoteCompletionFailed(remoteJobID, syncErr, retryAt)
}

func (s *Service) RemoteCompletionStatus() (RemoteCompletionStatus, error) {
	return s.store.RemoteCompletionStatus()
}

func isTerminalPrintStatus(status string) bool {
	switch status {
	case "completed", "done", "success", "failed", "cancelled", "interrupted":
		return true
	default:
		return false
	}
}
func first(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
	}
	return ""
}

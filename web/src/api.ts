// Typed client for ClinicHub.Api. Shapes mirror the C# records in src/ClinicHub.Api/Endpoints.

export type ClinicBrief = { id: number; slug: string; name: string }
export type Me = {
  id: number; userName: string; displayName: string; credentials?: string
  /** The clinic chosen after sign-in; its database serves every page. */
  clinic?: ClinicBrief; clinics: ClinicBrief[]
  roles: string[]; permissions: string[]; providerId?: number
  twoFactorEnabled: boolean; twoFactorRequired: boolean; pendingAction?: 'change-password' | 'setup-two-factor'; isPlatformAdmin: boolean
}
export type Settings = { demo: boolean; demoUsers: string[]; accountsLocked: boolean }

export type Status = 'Booked' | 'Arrived' | 'Roomed' | 'WithProvider' | 'Completed' | 'NoShow' | 'Cancelled'

export type PatientBrief = { id: number; name: string; mrn: string; age: number; sex: string; coverage: string }
export type Eligibility = { code: string; eligible: boolean; message: string; paymentProgram: string }

export type AppointmentRow = {
  id: number; startUtc: string; endUtc: string; status: Status; source: string; queueNumber?: number; reason?: string
  providerId: number; roomId?: number; room?: string; type: { name: string; color: string; minutes: number }
  patient: PatientBrief
  arrivedUtc?: string; roomedUtc?: string; withProviderUtc?: string; completedUtc?: string
  encounterId?: number; eligibility?: Eligibility
}

export type DayView = {
  date: string; locationId: number; location: string
  providers: { id: number; name: string; kind: string; specialty?: string }[]
  rooms: { id: number; name: string; kind: string }[]
  appointments: AppointmentRow[]
}

export class ApiError extends Error {
  constructor(public status: number, message: string, public fieldErrors: Record<string, string[]> = {}, public data: Record<string, unknown> = {}) {
    super(message)
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: { 'X-ClinicHub': '1', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (res.status === 204) return undefined as T
  const text = await res.text()
  const data = text ? JSON.parse(text) : undefined
  if (!res.ok) {
    if (res.status === 401 && !url.startsWith('/api/auth/')) window.dispatchEvent(new Event('clinichub:signed-out'))
    // Problem details: the specific message is in detail; title is the generic status text unless no detail was given.
    throw new ApiError(res.status, data?.detail ?? data?.title ?? `Request failed (${res.status})`, data?.errors ?? {}, data ?? {})
  }
  return data as T
}

export const api = {
  settings: () => request<Settings>('GET', '/api/settings'),
  me: () => request<Me | undefined>('GET', '/api/auth/me'),
  login: (userName: string, password: string, code?: string) => request<Me>('POST', '/api/auth/login', { userName, password, code: code ?? null }),
  selectClinic: (clinicId: number) => request<Me>('POST', '/api/auth/clinic', { clinicId }),
  changePassword: (currentPassword: string, newPassword: string) => request<Me>('POST', '/api/auth/password', { currentPassword, newPassword }),
  twoFactorSetup: () => request<{ secret: string; otpAuthUri: string }>('POST', '/api/auth/two-factor/setup'),
  twoFactorEnable: (code: string) => request<Me>('POST', '/api/auth/two-factor/enable', { code }),
  twoFactorDisable: (password: string) => request<Me>('POST', '/api/auth/two-factor/disable', { password }),
  revokeOtherSessions: () => request<void>('POST', '/api/auth/sessions/revoke'),
  /** Applications that sign in through ClinicHub and that this person may open from the current clinic. */
  apps: () => request<{ clientId: string; name: string; url?: string }[]>('GET', '/api/apps'),
  demoLogin: (userName: string) => request<Me>('POST', '/api/auth/demo-login', { userName }),
  logout: () => request<void>('POST', '/api/auth/logout'),
  day: (date?: string) => request<DayView>('GET', `/api/frontdesk/day${date ? `?date=${date}` : ''}`),
  checkIn: (id: number, presentedVersion?: string) =>
    request<{ eligibility: Eligibility }>('POST', `/api/frontdesk/appointments/${id}/check-in`, { presentedVersion: presentedVersion || null }),
  setStatus: (id: number, status: Status, roomId?: number, reason?: string) =>
    request<void>('POST', `/api/frontdesk/appointments/${id}/status`, { status, roomId: roomId ?? null, reason: reason ?? null }),
  patients: (q: string) => request<PatientBrief[]>('GET', `/api/patients?q=${encodeURIComponent(q)}`),
  addWalkIn: (patientId: number, reason: string) => request<{ id: number; queueNumber: number }>('POST', '/api/frontdesk/walk-ins', { patientId, reason }),
}

export const can = (me: Me, p: string) => me.permissions.includes(p)

export function localTime(iso: string) {
  return new Date(iso.endsWith('Z') ? iso : iso + 'Z').toLocaleTimeString('en-CA', { hour: '2-digit', minute: '2-digit', hour12: false, timeZone: 'America/Toronto' })
}

export function minutesSince(iso: string | undefined, now: number) {
  if (!iso) return 0
  return Math.max(0, Math.round((now - new Date(iso.endsWith('Z') ? iso : iso + 'Z').getTime()) / 60000))
}

export function torontoToday() {
  return new Date().toLocaleDateString('en-CA', { timeZone: 'America/Toronto' })
}

export function shiftDate(date: string, days: number) {
  const d = new Date(date + 'T12:00:00')
  d.setDate(d.getDate() + days)
  return d.toISOString().slice(0, 10)
}

// --- the visit ---

export type EncounterStatus = 'Open' | 'Signed' | 'Voided'
export type ProviderBrief = { id: number; name: string; kind: string; specialty?: string }

export type WorklistRow = {
  id: number; appointmentId?: number; patient: PatientBrief; providerId: number; kind: string; status: EncounterStatus; paymentProgram: string
  appointmentStatus?: Status; room?: string; reason?: string; checkInUtc: string; roomedUtc?: string; startedUtc?: string
  hasVitals: boolean; hasNote: boolean; coded: number
}
export type Worklist = { date: string; providers: ProviderBrief[]; visits: WorklistRow[]; olderUnsigned: WorklistRow[] }

export type Vitals = {
  id: number; takenUtc: string; systolicMmHg?: number; diastolicMmHg?: number; heartRate?: number; respiratoryRate?: number
  temperatureC?: number; spO2?: number; weightKg?: number; heightCm?: number; bmi?: number; takenBy: string
}
export type VitalsInput = Partial<Omit<Vitals, 'id' | 'takenUtc' | 'bmi' | 'takenBy'>>
export type Soap = { subjective?: string; objective?: string; assessment?: string; plan?: string }
export type Note = Soap & { addendum?: string; updatedUtc: string; author: string }
export type Diagnosis = { code: string; description: string; isPrimary: boolean }
export type Service = { feeCode?: string; uninsuredServiceId?: number; code: string; description: string; units: number; fee: number; insured: boolean }

export type Encounter = {
  id: number; status: EncounterStatus; kind: string; paymentProgram: string; reasonForVisit?: string
  checkInUtc: string; startedUtc?: string; endedUtc?: string; signedUtc?: string; signedBy?: string
  patient: { id: number; name: string; mrn: string; age: number; sex: string; dateOfBirth: string; coverage: string; healthCard?: string }
  provider: ProviderBrief; appointmentId?: number; appointmentStatus?: Status; room?: string; eligibility?: Eligibility
  vitals: Vitals[]; note?: Note; diagnoses: Diagnosis[]; services: Service[]; insuredTotal: number; uninsuredTotal: number
  readyToSign: string[]
  billing?: { claimNumber?: string; claimStatus?: string; claimAmount?: number; invoiceNumber?: string; invoiceTotal?: number }
  history: { id: number; checkInUtc: string; provider: string; status: EncounterStatus; diagnosis?: string; assessment?: string }[]
  /** Earlier visits the patient withheld from this person (PHIPA consent directive). */
  withheldVisits: number
}

export type Codes = {
  diagnostic: { code: string; description: string }[]
  fee: { code: string; description: string; fee: number }[]
  uninsured: { id: number; code: string; name: string; price: number }[]
}

export type Coding = {
  diagnoses: { code: string; isPrimary: boolean }[]
  services: { feeCode?: string; uninsuredServiceId?: number; units: number }[]
}

export const encounters = {
  worklist: (date?: string, providerId?: number | 'all') => {
    const q = new URLSearchParams()
    if (date) q.set('date', date)
    if (typeof providerId === 'number') q.set('providerId', String(providerId))
    // Without either, the server shows the signed-in provider's own visits.
    if (providerId === 'all') q.set('all', 'true')
    return request<Worklist>('GET', `/api/encounters?${q}`)
  },
  get: (id: number) => request<Encounter>('GET', `/api/encounters/${id}`),
  start: (id: number) => request<Encounter>('POST', `/api/encounters/${id}/start`),
  finish: (id: number) => request<Encounter>('POST', `/api/encounters/${id}/finish`),
  addVitals: (id: number, v: VitalsInput) => request<Encounter>('POST', `/api/encounters/${id}/vitals`, v),
  saveNote: (id: number, note: Soap) => request<{ updatedUtc: string }>('PUT', `/api/encounters/${id}/note`, note),
  saveCoding: (id: number, coding: Coding) => request<Encounter>('PUT', `/api/encounters/${id}/coding`, coding),
  sign: (id: number) => request<Encounter>('POST', `/api/encounters/${id}/sign`),
  addendum: (id: number, text: string) => request<Encounter>('POST', `/api/encounters/${id}/addendum`, { text }),
  codes: () => request<Codes>('GET', '/api/codes'),
}

export const money = (n: number) => n.toLocaleString('en-CA', { style: 'currency', currency: 'CAD' })

export function localDateTime(iso: string) {
  return new Date(iso.endsWith('Z') ? iso : iso + 'Z').toLocaleString('en-CA', {
    year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false, timeZone: 'America/Toronto',
  })
}

// --- OHIP billing ---

export type ClaimStatus = 'Draft' | 'Ready' | 'Submitted' | 'Paid' | 'PartiallyPaid' | 'Rejected'
export type Kpi = { count: number; amount: number }
export type BillingOverview = {
  ready: Kpi; unbatched: Kpi; submitted: Kpi; paidLast30Days: Kpi; rejected: Kpi; partiallyPaid: Kpi; invoicesOwing: Kpi
  providers: ProviderBrief[]; explanatoryCodes: Record<string, string>
}
export type ClaimRow = {
  id: number; accountingNumber: string; status: ClaimStatus; paymentProgram: string; patientId: number; patient: string; mrn: string
  providerId: number; provider: string; serviceDate: string; services: string; amountSubmitted: number; amountPaid: number
  batchFile?: string; encounterId: number; explanatoryCodes: string[]
}
export type ClaimDetail = {
  claim: ClaimRow; healthCard?: string; province?: string
  items: { serviceCode: string; description?: string; serviceDate: string; units: number; diagnosticCode?: string; feeSubmitted: number; feePaid?: number; explanatoryCode?: string; explanation?: string }[]
}
export type BatchRow = {
  id: number; providerId: number; provider: string; billingNumber?: string; batchNumber: number; fileName: string
  status: 'Created' | 'Submitted' | 'Accepted' | 'Rejected'; claimCount: number; total: number; createdUtc: string; submittedUtc?: string
}
export type RemittanceRow = { id: number; fileName: string; paymentDate: string; totalPaid: number; items: number; exceptions: number; unmatched: number; simulated: boolean; importedUtc: string }
export type RemittanceDetail = {
  remittance: RemittanceRow; messages: string[]
  providers: { providerId: number; provider: string; billingNumber?: string; claims: number; submitted: number; paid: number; overheadPercent: number; overhead: number; net: number }[]
  exceptions: { accountingNumber: string; claimId?: number; patient?: string; serviceCode: string; serviceDate: string; submitted: number; paid: number; explanatoryCode?: string; explanation: string }[]
}

export const billing = {
  overview: () => request<BillingOverview>('GET', '/api/billing/overview'),
  claims: (status?: string, providerId?: number) => {
    const q = new URLSearchParams()
    if (status) q.set('status', status)
    if (providerId) q.set('providerId', String(providerId))
    return request<ClaimRow[]>('GET', `/api/billing/claims?${q}`)
  },
  claim: (id: number) => request<ClaimDetail>('GET', `/api/billing/claims/${id}`),
  resubmit: (id: number) => request<void>('POST', `/api/billing/claims/${id}/resubmit`),
  batches: () => request<BatchRow[]>('GET', '/api/billing/batches'),
  createBatches: () => request<BatchRow[]>('POST', '/api/billing/batches'),
  submitBatch: (id: number) => request<BatchRow>('POST', `/api/billing/batches/${id}/submit`),
  batchFileUrl: (id: number) => `/api/billing/batches/${id}/file`,
  remittances: () => request<RemittanceRow[]>('GET', '/api/billing/remittances'),
  remittance: (id: number) => request<RemittanceDetail>('GET', `/api/billing/remittances/${id}`),
  simulateRemittance: () => request<RemittanceDetail>('POST', '/api/billing/remittances/simulate'),
  importRemittance: (fileName: string, content: string) => request<RemittanceDetail>('POST', '/api/billing/remittances', { fileName, content }),
  remittanceFileUrl: (id: number) => `/api/billing/remittances/${id}/file`,
}

export async function fetchText(url: string) {
  const res = await fetch(url, { credentials: 'same-origin' })
  if (!res.ok) throw new ApiError(res.status, `Could not load the file (${res.status}).`)
  return res.text()
}

// --- people, roles, logs, clinics ---

export type PermissionInfo = { key: string; group: string; label: string }
export type StaffUser = {
  id: number; userName: string; displayName: string; credentials?: string; isActive: boolean; roleIds: number[]; roles: string[]
  permissions: string[]; otherClinics: string[]; twoFactorEnabled: boolean; twoFactorRequired: boolean; mustChangePassword: boolean
  isLockedOut: boolean; lastLoginUtc?: string; createdUtc: string
}
export type RoleInfo = { id: number; name: string; description?: string; permissions: string[]; requireTwoFactor: boolean; isSystem: boolean; userCount: number }
export type SaveRole = { name: string; description: string | null; permissions: string[]; requireTwoFactor: boolean }
export type AuditRow = { id: number; atUtc: string; type: string; actor?: string; target?: string; detail?: string; ipAddress?: string }
export type AccessRow = { id: number; atUtc: string; user: string; patient: string; mrn: string; action: string; context: string; ipAddress?: string; override: boolean }
export type ClinicInfo = { id: number; slug: string; name: string; databaseName: string; isActive: boolean; members: number; createdUtc: string }

export const admin = {
  permissions: () => request<PermissionInfo[]>('GET', '/api/permissions'),
  users: () => request<StaffUser[]>('GET', '/api/users'),
  createUser: (u: { userName: string; displayName?: string; credentials?: string | null; roleIds: number[]; password?: string }) =>
    request<StaffUser>('POST', '/api/users', u),
  updateUser: (id: number, u: { displayName: string; credentials: string | null; roleIds: number[] }) => request<StaffUser | undefined>('PUT', `/api/users/${id}`, u),
  resetPassword: (id: number, password: string) => request<void>('POST', `/api/users/${id}/password`, { password }),
  unlock: (id: number) => request<void>('POST', `/api/users/${id}/unlock`),
  resetTwoFactor: (id: number) => request<void>('POST', `/api/users/${id}/two-factor/reset`),
  revokeSessions: (id: number) => request<void>('POST', `/api/users/${id}/sessions/revoke`),
  roles: () => request<RoleInfo[]>('GET', '/api/roles'),
  createRole: (r: SaveRole) => request<RoleInfo>('POST', '/api/roles', r),
  updateRole: (id: number, r: SaveRole) => request<RoleInfo>('PUT', `/api/roles/${id}`, r),
  deleteRole: (id: number) => request<void>('DELETE', `/api/roles/${id}`),
  audit: (q: { type?: string; before?: number }) => {
    const p = new URLSearchParams()
    if (q.type) p.set('type', q.type)
    if (q.before) p.set('before', String(q.before))
    return request<AuditRow[]>('GET', `/api/audit?${p}`)
  },
  access: (before?: number) => request<AccessRow[]>('GET', `/api/audit/access${before ? `?before=${before}` : ''}`),
  clinics: () => request<ClinicInfo[]>('GET', '/api/platform/clinics'),
  createClinic: (slug: string, name: string) => request<ClinicInfo>('POST', '/api/platform/clinics', { slug, name }),
}

export const firstError = (err: unknown, fallback: string) =>
  err instanceof ApiError ? Object.values(err.fieldErrors).flat()[0] ?? err.message : fallback

// --- orders to LabFlow and ClinicReport (FHIR) ---

export type CatalogItem = { target: 'LabFlow' | 'ClinicReport' | 'ExternalLab'; code: string; display: string; group: string; connected: boolean }
export type OrderStatus = 'Draft' | 'AwaitingResult' | 'Accepted' | 'InProgress' | 'Completed' | 'Cancelled' | 'Failed' | 'Cancelling'
export type Order = {
  id: number; target: 'LabFlow' | 'ClinicReport' | 'ExternalLab'; code: string; display: string; priority: 'Routine' | 'Stat'; status: OrderStatus
  notes?: string; createdUtc: string; sentUtc?: string; externalId?: string; requester: string; attempts: number; lastError?: string
  result?: { id: number; status: string; isAbnormal: boolean; summary?: string; externalUrl?: string; receivedUtc: string; reviewedUtc?: string }
  cancelReason?: string; statusNote?: string
}

export const orders = {
  catalog: () => request<CatalogItem[]>('GET', '/api/orders/catalog'),
  forEncounter: (encounterId: number) => request<Order[]>('GET', `/api/encounters/${encounterId}/orders`),
  place: (encounterId: number, items: { target: string; code: string }[], priority: 'Routine' | 'Stat', notes: string) =>
    request<Order[]>('POST', `/api/encounters/${encounterId}/orders`, { items, priority, notes }),
  resend: (id: number) => request<void>('POST', `/api/orders/${id}/resend`),
  cancel: (id: number, reason: string) => request<Order[]>('POST', `/api/orders/${id}/cancel`, { reason }),
}

// --- electronic prescriptions to the pharmacy (FHIR MedicationRequest) ---

export type Drug = { din: string; display: string; unit: string; group: string; connected: boolean }
export type PrescriptionStatus = 'Draft' | 'Accepted' | 'Cancelled' | 'Failed' | 'Cancelling'
export type Prescription = {
  id: number; din: string; display: string; directions: string; quantity: number; unit: string; daysSupply?: number; repeats: number
  noSubstitution: boolean; note?: string; status: PrescriptionStatus; createdUtc: string; sentUtc?: string; externalId?: string
  prescriber: string; attempts: number; lastError?: string; cancelReason?: string; statusNote?: string
}
export type PrescriptionLine = {
  din: string; directions: string; quantity: number; daysSupply?: number; repeats: number; noSubstitution: boolean; note?: string
}

/** What is said before a drug goes out: a recorded allergy, an interaction with what the patient takes, or two of a kind. */
export type DrugAlert = { key: string; kind: 'Allergy' | 'Interaction' | 'Duplicate'; severity: 'Severe' | 'Major' | 'Moderate'; message: string; dins: string[] }
export type AlertOverride = { key: string; reason?: string }
export type Medication = {
  id: number; din: string; display: string; directions: string; quantity: number; unit: string; daysSupply?: number; repeats: number; status: PrescriptionStatus
  createdUtc: string; prescriber: string; runsOutUtc: string; current: boolean; canRenew: boolean; renewedBy?: number
}

export const prescriptions = {
  catalog: () => request<Drug[]>('GET', '/api/prescriptions/catalog'),
  forEncounter: (encounterId: number) => request<Prescription[]>('GET', `/api/encounters/${encounterId}/prescriptions`),
  write: (encounterId: number, items: PrescriptionLine[], overrides?: AlertOverride[]) =>
    request<Prescription[]>('POST', `/api/encounters/${encounterId}/prescriptions`, { items, overrides }),
  check: (encounterId: number, items: PrescriptionLine[]) =>
    request<{ alerts: DrugAlert[]; someWithheld: boolean; allergiesRecorded: boolean }>('POST', `/api/encounters/${encounterId}/prescriptions/check`, { items }),
  medications: (patientId: number) => request<{ items: Medication[]; someWithheld: boolean }>('GET', `/api/patients/${patientId}/medications`),
  renew: (id: number, encounterId: number, overrides?: AlertOverride[]) =>
    request<Prescription[]>('POST', `/api/prescriptions/${id}/renew`, { encounterId, overrides }),
  resend: (id: number) => request<void>('POST', `/api/prescriptions/${id}/resend`),
  cancel: (id: number, reason: string) => request<Prescription[]>('POST', `/api/prescriptions/${id}/cancel`, { reason }),
}

// --- results back (FHIR DiagnosticReport) ---

export type ResultRow = {
  id: number; receivedUtc: string; status: 'Preliminary' | 'Final' | 'Amended' | 'Cancelled'; isAbnormal: boolean; summary?: string; externalUrl?: string
  orderId: number; order: string; target: 'LabFlow' | 'ClinicReport' | 'ExternalLab'; patientId: number; patient: string; mrn: string; encounterId: number
  requester: string; reviewedUtc?: string; reviewedBy?: string; withheld: boolean
}

export const results = {
  inbox: (all = false, reviewed = false) => request<ResultRow[]>('GET', `/api/results?all=${all}&reviewed=${reviewed}`),
  review: (id: number) => request<void>('POST', `/api/results/${id}/review`),
}

// --- the master patient index: registration, corrections, duplicates, merge ---

export type PatientFields = {
  firstName: string; middleName?: string | null; lastName: string; preferredName?: string | null; dateOfBirth?: string | null; sex?: string | null
  healthCardNumber?: string | null; healthCardVersion?: string | null; healthCardExpiry?: string | null; coverageProvince?: string | null; outOfProvinceNumber?: string | null
  phone?: string | null; email?: string | null; addressLine1?: string | null; city?: string | null; province?: string | null; postalCode?: string | null
  preferredLanguage?: string | null; familyProviderName?: string | null; familyProviderOhipNumber?: string | null; isDeceased: boolean; deceasedDate?: string | null
}
export type PatientDetail = {
  id: number; mrn: string; name: string; age: number; coverage: string; fields: PatientFields
  mergedInto?: PatientBrief; mergedFrom: PatientBrief[]; visits: number; lastVisitUtc?: string; openDuplicates: number
  createdUtc: string; updatedUtc: string; rowVersion: string
}
export type PossibleDuplicate = { patient: PatientBrief; dateOfBirth: string; phone?: string; score: number; reasons: string[] }
export type DuplicateStatus = 'Open' | 'Merged' | 'NotDuplicate'
export type DuplicateRow = {
  id: number; score: number; reasons: string[]; status: DuplicateStatus; foundUtc: string; a: PatientBrief; b: PatientBrief; reviewedBy?: string; reviewedUtc?: string
}
export type DuplicatePair = { row: DuplicateRow; a: PatientDetail; b: PatientDetail }

export const mpi = {
  get: (id: number) => request<PatientDetail>('GET', `/api/patients/${id}`),
  register: (patient: PatientFields, confirmNew: boolean) => request<PatientDetail>('POST', '/api/patients', { patient, confirmNew }),
  update: (id: number, patient: PatientFields, rowVersion: string) => request<PatientDetail>('PUT', `/api/patients/${id}`, { patient, rowVersion }),
  duplicates: (status: DuplicateStatus = 'Open') => request<DuplicateRow[]>('GET', `/api/patients/duplicates?status=${status}`),
  pair: (id: number) => request<DuplicatePair>('GET', `/api/patients/duplicates/${id}`),
  scan: () => request<{ found: number; open: number }>('POST', '/api/patients/duplicates/scan'),
  notDuplicate: (id: number) => request<void>('POST', `/api/patients/duplicates/${id}/not-duplicate`),
  merge: (survivorId: number, mergedId: number) => request<PatientDetail>('POST', '/api/patients/merge', { survivorId, mergedId }),
  unmerge: (id: number) => request<PatientDetail>('POST', `/api/patients/${id}/unmerge`),
}

// --- PHIPA consent directives ---

export type ConsentScope = 'EntireRecord' | 'Encounter' | 'Provider'
export type Directive = {
  id: number; scope: ConsentScope; encounterId?: number; encounterUtc?: string; encounterProvider?: string; excludedProviderId?: number
  excludedProvider?: string; note?: string; recordedBy: string; createdUtc: string; revokedUtc?: string; overrides: number
}
export type ConsentInfo = {
  directives: Directive[]; visits: { id: number; checkInUtc: string; provider: string }[]; providers: { id: number; name: string }[]; overrideUntilUtc?: string
}
export type OverrideRow = { id: number; atUtc: string; expiresUtc: string; user: string; patientId: number; patient: string; mrn: string; scope: ConsentScope; reason: string }
/** The 403 a visit withheld by a directive answers with. */
export type Withheld = { consentWithheld: true; canOverride: boolean; patientId: number; patient: string; mrn: string }

export const consent = {
  get: (patientId: number) => request<ConsentInfo>('GET', `/api/patients/${patientId}/consent`),
  record: (patientId: number, d: { scope: ConsentScope; encounterId?: number | null; excludedProviderId?: number | null; note?: string | null }) =>
    request<Directive[]>('POST', `/api/patients/${patientId}/consent`, d),
  revoke: (id: number) => request<Directive[]>('POST', `/api/consent/${id}/revoke`),
  override: (patientId: number, reason: string) => request<{ expiresUtc: string }>('POST', `/api/patients/${patientId}/consent/override`, { reason }),
  overrides: () => request<OverrideRow[]>('GET', '/api/consent/overrides'),
}

export const withheld = (e: unknown): Withheld | null =>
  e instanceof ApiError && e.status === 403 && e.data.consentWithheld ? e.data as unknown as Withheld : null

export const CONSENT_SCOPES: Record<ConsentScope, string> = { EntireRecord: 'Entire record', Encounter: 'One visit', Provider: 'Kept from one provider' }

// --- allergies ---

export type Allergy = { id: number; allergenKey?: string; substance: string; reaction?: string; severity: 'Mild' | 'Moderate' | 'Severe'; createdUtc: string; recordedBy: string }
export type AllergyList = { recorded: boolean; noKnownAllergies: boolean; items: Allergy[] }

export const allergies = {
  allergens: () => request<{ key: string; label: string }[]>('GET', '/api/allergens'),
  get: (patientId: number) => request<AllergyList>('GET', `/api/patients/${patientId}/allergies`),
  add: (patientId: number, a: { allergenKey?: string; substance?: string; reaction?: string; severity: string }) =>
    request<AllergyList>('POST', `/api/patients/${patientId}/allergies`, a),
  none: (patientId: number) => request<AllergyList>('POST', `/api/patients/${patientId}/allergies/none`),
  end: (id: number, reason: string) => request<AllergyList>('POST', `/api/allergies/${id}/end`, { reason }),
}

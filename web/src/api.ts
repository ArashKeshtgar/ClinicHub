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
export type AccessRow = { id: number; atUtc: string; user: string; patient: string; mrn: string; action: string; context: string; ipAddress?: string }
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

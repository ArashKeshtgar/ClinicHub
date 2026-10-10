// Typed client for ClinicHub.Api. Shapes mirror the C# records in src/ClinicHub.Api/Endpoints.

export type Me = { id: number; userName: string; displayName: string; roles: string[]; permissions: string[]; providerId?: number }
export type Settings = { demo: boolean; demoUsers: string[] }

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
  constructor(public status: number, message: string, public fieldErrors: Record<string, string[]> = {}) {
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
    throw new ApiError(res.status, data?.detail ?? data?.title ?? `Request failed (${res.status})`, data?.errors ?? {})
  }
  return data as T
}

export const api = {
  settings: () => request<Settings>('GET', '/api/settings'),
  me: () => request<Me | undefined>('GET', '/api/auth/me'),
  login: (userName: string, password: string) => request<Me>('POST', '/api/auth/login', { userName, password }),
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

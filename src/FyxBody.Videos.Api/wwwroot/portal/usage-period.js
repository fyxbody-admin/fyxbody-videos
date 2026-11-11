// Gregorian UTC reporting period. Labels use the portal locale; identifiers keep
// the API's ASCII YYYY-MM format independently of locale and operating system.
export const usageMonthIds = Object.freeze(Array.from({ length: 12 }, (_, index) => String(index + 1).padStart(2, '0')));
export function formatUsageMonth(locale, month) {
  if (!usageMonthIds.includes(month)) throw new RangeError('Invalid usage month');
  return new Intl.DateTimeFormat(locale, { calendar: 'gregory', month: 'long', timeZone: 'UTC' }).format(new Date(`2000-${month}-01T00:00:00Z`));
}
export function usagePeriodValue(year, month) {
  const numericYear = Number(year);
  if (!Number.isInteger(numericYear) || numericYear < 1 || numericYear > 9998 || !usageMonthIds.includes(month)) return null;
  return `${String(numericYear).padStart(4, '0')}-${month}`;
}
export function parseUsagePeriod(value) {
  if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(value)) return null;
  const year = Number(value.slice(0, 4)), month = value.slice(5);
  return usagePeriodValue(year, month) ? { year, month } : null;
}

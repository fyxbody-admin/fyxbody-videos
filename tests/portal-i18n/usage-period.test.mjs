import test from 'node:test';
import assert from 'node:assert/strict';
import { usageMonthIds, formatUsageMonth, usagePeriodValue, parseUsagePeriod } from '../../src/FyxBody.Videos.Api/wwwroot/portal/usage-period.js';

test('usage month names follow portal locale and Gregorian UTC independent of OS', () => {
  const expected = { es: 'noviembre', ca: 'novembre', en: 'November', ar: 'نوفمبر' };
  for (const [locale, name] of Object.entries(expected)) {
    assert.equal(formatUsageMonth(locale, '11'), name);
    for (const month of usageMonthIds) {
      assert.equal(formatUsageMonth(locale, month), new Intl.DateTimeFormat(locale, { calendar: 'gregory', month: 'long', timeZone: 'UTC' }).format(new Date(`2000-${month}-01T00:00:00Z`)));
    }
  }
});
test('month selection and year edits retain ASCII YYYY-MM API/CSV period identity', () => {
  assert.deepEqual(parseUsagePeriod('2026-11'), { year: 2026, month: '11' });
  assert.equal(usagePeriodValue('2026', '12'), '2026-12');
  assert.equal(usagePeriodValue('2027', '01'), '2027-01');
  assert.equal(usagePeriodValue(1, '01'), '0001-01');
  assert.equal(usagePeriodValue(9998, '12'), '9998-12');
  for (const year of ['', 0, 9999, '2026.5', 'not-a-year']) assert.equal(usagePeriodValue(year, '11'), null);
  for (const month of ['00', '13', '1', '١١']) assert.equal(usagePeriodValue(2026, month), null);
  for (const value of ['0000-01', '9999-01', '2026-13', '2026-1']) assert.equal(parseUsagePeriod(value), null);
  assert.throws(() => formatUsageMonth('ar', '13'), RangeError);
});

const dateFormatter = new Intl.DateTimeFormat('en-GB', {
  timeZone: 'Europe/Kyiv',
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
});

export function exportFilename(name: string, date: Date = new Date()): string {
  return `${name}-${dateFormatter.format(date).replaceAll('/', '-')}.xlsx`;
}

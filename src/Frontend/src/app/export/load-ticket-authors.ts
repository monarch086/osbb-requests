import type { ManagedTicket, RequestAuthor } from '../api.models';

export async function loadTicketAuthors(
  tickets: ManagedTicket[],
  getTicket: (ticket: ManagedTicket) => Promise<ManagedTicket>,
  isCurrent: () => boolean,
): Promise<ManagedTicket[] | null> {
  const unique = [...new Map(tickets.map((ticket) => [ticket.request.ownerId, ticket])).entries()];
  const authors = new Map<number, RequestAuthor | null>();
  let index = 0;
  async function worker(): Promise<void> {
    while (isCurrent() && index < unique.length) {
      const [ownerId, ticket] = unique[index++];
      const detail = await getTicket(ticket);
      if (!isCurrent()) {
        return;
      }
      authors.set(ownerId, detail.author ?? null);
    }
  }
  await Promise.all(Array.from({ length: Math.min(8, unique.length) }, () => worker()));
  return isCurrent()
    ? tickets.map((ticket) => ({ ...ticket, author: authors.get(ticket.request.ownerId) ?? null }))
    : null;
}

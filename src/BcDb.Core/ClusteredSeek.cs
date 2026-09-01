using System.Buffers.Binary;

namespace BusinessCentral.DbReader;

/// <summary>
/// Finds the leaf page where a clustered-index key begins, by descending the index,
/// instead of scanning the whole leaf level.
///
/// Why this exists: syscolpars, sysiscols and sysrscols are clustered on exactly the
/// value the reader looks a table up by (object id, object id, rowset id), yet answering
/// "the columns of one table" scanned all of them — 2,840 of the 5,196 catalog leaf pages
/// a single-table read touches on the BC 28.1 demo backup.
///
/// Non-leaf record layout (derived from DBCC PAGE dumps of the bc281 clustered index roots
/// of syscolpars 1:46387, sysrscols 1:48121 and sysiscols 1:148 — three different key
/// shapes — and cross-read against DBCC's own decode of every field; PROVENANCE.md
/// "Clustered index descent"):
///
///   [status byte][key columns, packed, in key order, each at its storage width][child]
///
/// with the child a 6-byte page pointer (u32 page id, u16 file id) at the end of the
/// record. Records on an index page are fixed width and that width is the page header's
/// pminlen at offset 14, so the key width need not be known independently: the child
/// pointer sits at pminlen - 6. Verified: pminlen is 17/19/19/11/15/15 against key widths
/// 10/12/12/4/8/8 for syscolpars/sysiscols/sysrscols/sysschobjs/sysallocunits/sysrowsets.
///
/// Levels: a type-2 page at m_level 0 is the lowest index level and its children are the
/// type-1 leaf pages. The descent therefore stops on page type, never on a level count.
///
/// The slot array is in key order (DBCC's row dump of each root is ascending), and slot 0
/// of every index page is the leftmost pointer, whose key bytes are not a key — DBCC
/// renders them NULL. Treating slot 0 as "less than everything" is always safe: the parent
/// already guaranteed the target belongs somewhere in this page's range.
/// </summary>
internal static class ClusteredSeek
{
    const int PminlenOffset = 14;      // page header, confirmed against DBCC's pminlen
    const int ChildPointerBytes = 6;
    const int MaxDepth = 16;           // a B-tree over a catalog table is 1-3 levels

    /// <summary>
    /// The first leaf page that can hold <paramref name="key"/>, or null when the index
    /// has a shape this derivation does not cover and the caller should scan instead.
    /// Returning null can only cost time; every caller has a scan that produces the same
    /// rows. A structure that contradicts the derivation throws instead.
    /// </summary>
    public static byte[]? FindLeaf(PageFile pf, AllocUnit au, IReadOnlySet<int> auPages,
                                   long key, int leadWidth, string what)
    {
        var (pid, fid) = Catalog.ReadPagePtr(au.RootPage, 0);
        // root_page is one of the pointers the stale-metadata rule distrusts: on a shrunk
        // database it can name a page another object now owns. The IAM page set is the
        // trustworthy one, so a root outside it is treated as stale and scanned instead.
        if (fid != 1 || pid <= 0 || !auPages.Contains(pid)) return null;

        var page = pf.GetPage(1, pid);
        for (int depth = 0; ; depth++)
        {
            byte type = PageHeader.Type(page);
            if (type == 1) return page;                       // leaf level
            if (type != 2)
                throw new InvalidDataException(
                    $"{what}: page 1:{pid} reached by descending the clustered index is type {type}, not an index or data page — refusing to guess");
            if (depth > MaxDepth)
                throw new InvalidDataException($"{what}: clustered index deeper than {MaxDepth} levels — refusing to guess");
            // Ghosted index records are a shape this has not been derived for: their child
            // pointers may name pages that are no longer part of the tree.
            if (PageHeader.GhostRecords(page) != 0) return null;

            int pminlen = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(PminlenOffset));
            if (pminlen < 1 + leadWidth + ChildPointerBytes || pminlen > PageFile.PageSize - 96) return null;

            pid = ChildFor(page, pminlen, key, leadWidth, what);
            page = pf.GetPage(1, pid);
        }
    }

    /// <summary>
    /// The child holding the largest key strictly below <paramref name="key"/>.
    ///
    /// Strictly below, not "at most": only the leading column of a composite key is
    /// compared here, and rows sharing a leading value straddle page boundaries — the
    /// columns of one object run over several syscolpars pages. Descending to the last
    /// child whose key is &lt;= the target would skip the earlier pages of that run. This
    /// lands on the page holding the last row before the target instead, so a seek reads
    /// at most one leaf page that turns out to hold nothing wanted.
    /// </summary>
    static int ChildFor(byte[] page, int pminlen, long key, int leadWidth, string what)
    {
        int slots = PageHeader.SlotCount(page);
        if (slots < 1)
            throw new InvalidDataException($"{what}: index page with no slots — refusing to guess");

        int chosen = -1;
        long previous = 0;
        bool havePrevious = false;
        for (int s = 0; s < slots; s++)
        {
            int off = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(PageFile.PageSize - 2 * (s + 1)));
            if (off < 96 || off + pminlen > PageFile.PageSize)
                throw new InvalidDataException($"{what}: index slot offset {off} does not hold a {pminlen}-byte record — refusing to guess");
            if (s == 0) { chosen = off; continue; }            // leftmost pointer: no key

            long slotKey = leadWidth == 8
                ? BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(off + 1))
                : BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(off + 1));
            // Keys ascend with the slot array. If they do not, this is not reading keys
            // where the keys are, and every answer built on it would be wrong.
            if (havePrevious && slotKey < previous)
                throw new InvalidDataException(
                    $"{what}: clustered index keys are not ascending across the slot array ({slotKey} after {previous}) — refusing to guess");
            previous = slotKey;
            havePrevious = true;

            if (slotKey >= key) break;
            chosen = off;
        }

        int childOffset = chosen + pminlen - ChildPointerBytes;
        int childPid = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(childOffset));
        int childFid = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(childOffset + 4));
        if (childFid != 1 || childPid <= 0)
            throw new InvalidDataException($"{what}: clustered index child pointer ({childFid}:{childPid}) is not a page of this file — refusing to guess");
        return childPid;
    }
}

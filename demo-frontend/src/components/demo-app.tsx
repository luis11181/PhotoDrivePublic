import {
  Album,
  ArrowRight,
  Check,
  Cloud,
  ExternalLink,
  FileCheck2,
  Files,
  FolderTree,
  Github,
  Heart,
  Images,
  KeyRound,
  Languages,
  Search,
  Share2,
  ShieldCheck,
  SlidersHorizontal,
  Sparkles,
  UserRoundPlus,
} from "lucide-react";
import Image from "next/image";

const sourceUrl = "https://github.com/luis11181/PhotoDrivePublic";

const productAreas = [
  {
    icon: Images,
    label: "Photos",
    title: "Your memories, in a familiar timeline.",
    copy: "Browse years and months naturally, then narrow a large library without losing your place.",
    features: [
      { icon: SlidersHorizontal, text: "Sort and filter by date, type, tags, or favorites" },
      { icon: Search, text: "Search photos, timeline events, and capture dates" },
      { icon: Album, text: "Curate albums without making another copy" },
      { icon: Heart, text: "Favorites, responsive previews, and a full-screen viewer" },
    ],
  },
  {
    icon: Files,
    label: "Drive",
    title: "Real folders for everything else.",
    copy: "Keep documents and media together in one catalog, with a proper file workspace beside Photos.",
    features: [
      { icon: FolderTree, text: "Folders, breadcrumbs, sorting, and recursive search" },
      { icon: Cloud, text: "Resumable file and whole-folder uploads" },
      { icon: FileCheck2, text: "Inline previews, batch actions, and recoverable Trash" },
      { icon: Share2, text: "Share folders or selected albums privately" },
    ],
  },
];

export function DemoApp() {
  return (
    <div className="site-shell">
      <header className="site-header">
        <a className="site-brand" href="#top" aria-label="PhotoDrive home">
          <span><ShieldCheck /></span>
          <strong>PhotoDrive</strong>
          <small>self-hosted</small>
        </a>
        <nav aria-label="Project sections">
          <a href="#product">What it does</a>
          <a href="#family">For families</a>
          <a href="#safety">Privacy &amp; recovery</a>
        </nav>
        <a className="header-source" href={sourceUrl} target="_blank" rel="noreferrer">
          <Github /> <span>View source</span>
        </a>
      </header>

      <main id="top">
        <div className="demo-boundary">
          <span><Sparkles /> Frontend-only demo</span>
          <p>This public showcase uses synthetic media and never connects to a private PhotoDrive library.</p>
        </div>

        <section className="hero">
          <div className="hero-copy">
            <p className="eyebrow">Photos + Drive, built for one family</p>
            <h1>Your family library. On your terms.</h1>
            <p className="hero-intro">
              PhotoDrive brings the familiar photo timeline and cloud-drive experience into one private, self-hosted home for the people you trust.
            </p>
            <div className="hero-actions">
              <a className="primary-action" href={sourceUrl} target="_blank" rel="noreferrer">
                <Github /> Explore on GitHub <ArrowRight />
              </a>
              <a className="secondary-action" href="#product">See what it does</a>
            </div>
            <ul className="hero-points" aria-label="PhotoDrive highlights">
              <li><Check /> One library for photos and files</li>
              <li><Check /> Invite-only family access</li>
              <li><Check /> Backups designed in from day one</li>
            </ul>
          </div>

          <figure className="hero-product">
            <div className="window-bar"><i /><i /><i /><span>PhotoDrive · Photos</span></div>
            <a href="/demo/full-library-preview.webp" target="_blank" rel="noreferrer" aria-label="Open a full-size screenshot of PhotoDrive">
              <Image
                src="/demo/full-library-preview.webp"
                alt="PhotoDrive showing a photo timeline with year and month groups, filters, albums, files, sharing, events, and synthetic media"
                width={1415}
                height={1111}
                priority
                sizes="(max-width: 820px) 100vw, 58vw"
              />
            </a>
            <figcaption><span>The running Photos experience</span><span>Synthetic demo media</span></figcaption>
          </figure>
        </section>

        <section className="promise-strip" aria-label="Product summary">
          <article><Images /><div><strong>Like a photo app</strong><p>Timeline, albums, search, events, tags, and favorites.</p></div></article>
          <article><FolderTree /><div><strong>Like a drive</strong><p>Folders, documents, previews, uploads, and sorting.</p></div></article>
          <article><ShieldCheck /><div><strong>Unlike a black box</strong><p>Your host, your accounts, your storage, your recovery plan.</p></div></article>
        </section>

        <section className="product section-block" id="product">
          <header className="section-heading">
            <div>
              <p className="eyebrow">One simple home</p>
              <h2>The best parts of a photo library and a file drive.</h2>
            </div>
            <p>Photos and Files are two clean views over the same originals, so organizing an album or folder never wastes storage by duplicating bytes.</p>
          </header>

          <div className="product-grid">
            {productAreas.map((area) => {
              const AreaIcon = area.icon;
              return (
                <article className="product-card" key={area.label}>
                  <div className="product-card-heading">
                    <span><AreaIcon /></span>
                    <div><small>{area.label}</small><h3>{area.title}</h3></div>
                  </div>
                  <p>{area.copy}</p>
                  <ul>
                    {area.features.map((feature) => {
                      const FeatureIcon = feature.icon;
                      return <li key={feature.text}><FeatureIcon /><span>{feature.text}</span></li>;
                    })}
                  </ul>
                </article>
              );
            })}
          </div>
        </section>

        <section className="family section-block" id="family">
          <div className="family-intro">
            <p className="eyebrow">Made for a household, not a platform</p>
            <h2>Private sharing without account chaos.</h2>
            <p>Create exactly the family accounts you want, decide what each person can see, and keep sign-in familiar.</p>
          </div>
          <div className="family-features">
            <article><UserRoundPlus /><div><strong>You define the users</strong><p>Owners create, manage, or disable invite-only family accounts.</p></div></article>
            <article><Share2 /><div><strong>Share only what you mean to</strong><p>Give another family member access to a folder or a curated album.</p></div></article>
            <article><KeyRound /><div><strong>Simple sign-in</strong><p>Allow Google OAuth or a password. Google sign-in never creates an uninvited account.</p></div></article>
            <article><Languages /><div><strong>Comfortable for everyone</strong><p>English and Spanish, light and dark themes, and a responsive interface.</p></div></article>
          </div>
        </section>

        <section className="safety section-block" id="safety">
          <div className="safety-copy">
            <p className="eyebrow">Private by default. Recoverable by design.</p>
            <h2>Your memories deserve more than a single hard drive.</h2>
            <p>PhotoDrive separates durable originals, the catalog your family recognizes, and a replaceable local speed layer. Each part has one clear job.</p>
            <div className="storage-model" aria-label="Storage model">
              <span><Cloud /><strong>Private S3</strong><small>immutable originals</small></span>
              <i />
              <span><FileCheck2 /><strong>SQLite</strong><small>folders &amp; metadata</small></span>
              <i />
              <span><Images /><strong>Local cache</strong><small>safe to rebuild</small></span>
            </div>
          </div>
          <div className="safety-list">
            <article><span>01</span><div><h3>Verified backups</h3><p>Matched catalog and user snapshots are restored, integrity-checked, uploaded, and remotely verified twice daily.</p></div></article>
            <article><span>02</span><div><h3>Immutable, checked originals</h3><p>Private S3 keeps content-addressed originals outside the app host, with SHA-256 verification during ingestion.</p></div></article>
            <article><span>03</span><div><h3>Credentials stay private</h3><p>The browser never receives an S3 key or credential, and sign-in sessions remain in secure HttpOnly cookies.</p></div></article>
            <article><span>04</span><div><h3>Recovery you can understand</h3><p>Trash is manual and recoverable, caches are disposable, and owner tools make backup readiness visible.</p></div></article>
          </div>
        </section>

        <section className="fork-cta section-block">
          <div><p className="eyebrow">Make it yours</p><h2>Your photos. Your files. Your family&apos;s place.</h2><p>PhotoDrive is a personal project you can self-host and shape around your own infrastructure.</p></div>
          <a href={sourceUrl} target="_blank" rel="noreferrer"><Github /> Open PhotoDrivePublic <ExternalLink /></a>
        </section>
      </main>

      <footer>
        <a className="site-brand" href="#top"><span><ShieldCheck /></span><strong>PhotoDrive</strong></a>
        <p>Personal project showcase · static frontend · synthetic media only</p>
        <a href={sourceUrl} target="_blank" rel="noreferrer">Source <ExternalLink /></a>
      </footer>
    </div>
  );
}

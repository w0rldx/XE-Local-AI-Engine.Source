import Layout from '@theme/Layout';
import Link from '@docusaurus/Link';
import notices from '@site/src/data/third-party-notices.generated.json';

// Every value is rendered as a React text child, so package-supplied text is escaped, never parsed as HTML or MDX.
function Notice({ pkg }) {
  const id = `${pkg.name}@${pkg.version}`;
  return (
    <li className="margin-bottom--md">
      <strong>{id}</strong> ({pkg.license})
      {pkg.homepage && <> · <Link href={pkg.homepage}>homepage</Link></>}
      {pkg.repository && <> · <Link href={pkg.repository}>repository</Link></>}
      {pkg.spdx?.map((url) => <span key={url}> · <Link href={url}>{url.replace(/^.*\/(.*)\.html$/, '$1')}</Link></span>)}
      {pkg.author && <> · author {pkg.author}</>}
      {pkg.note && <p className="margin-bottom--none">{pkg.note}</p>}
      {pkg.missingText && <p className="margin-bottom--none">{pkg.missingText}</p>}
      {pkg.texts.map((text) => (
        <details key={text.file}>
          <summary>{text.file}</summary>
          <pre style={{ whiteSpace: 'pre-wrap' }}>{text.text}</pre>
        </details>
      ))}
    </li>
  );
}

export default function ThirdPartyNotices() {
  return (
    <Layout title="Third-party notices" description="Open-source packages used to build this website">
      <main className="container margin-vert--xl">
        <h1>Third-party notices</h1>
        <p>
          This website is built with the {notices.packages.length} open-source packages listed below, with the license
          and notice texts each package ships. The XE Local AI Engine releases carry their own third-party notices.
        </p>
        <ul>
          {notices.packages.map((pkg) => <Notice key={`${pkg.name}@${pkg.version}`} pkg={pkg} />)}
        </ul>
      </main>
    </Layout>
  );
}

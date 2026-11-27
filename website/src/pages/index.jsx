import Layout from '@theme/Layout';
import Link from '@docusaurus/Link';

const REPO = 'https://github.com/w0rldx/XE-Local-AI-Engine.Source/blob/develop';

export default function Home() {
  return (
    <Layout title="XE Local AI Engine" description="Documentation for XE Local AI Engine">
      <main className="container margin-vert--xl">
        <h1>XE Local AI Engine</h1>
        <p>
          A local AI node that runs chat, images, speech and training on your own machine. These pages are the
          documentation from the repository's <code>docs/</code> folder.
        </p>
        <ul>
          <li><Link to="/docs/user-guide">User guide</Link>: install, first run and everyday use.</li>
          <li><Link to="/docs/wiki/Home">Wiki</Link>: architecture and how the node works.</li>
          <li><Link to="/docs/adr">Architecture decision records</Link></li>
          <li><Link href={`${REPO}/README.md`}>README</Link> and <Link href={`${REPO}/CONTRIBUTING.md`}>contributing guide</Link> on GitHub</li>
        </ul>
      </main>
    </Layout>
  );
}
